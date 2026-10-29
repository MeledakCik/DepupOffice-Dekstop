using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace DepupOfficeDesktop.Core
{
    /// <summary>
    /// Porting dari Vault (VaultActivity.kt):
    /// File dienkripsi AES-256-GCM dengan kunci master yang dilindungi Windows DPAPI (ProtectedData);
    /// UI dikunci dengan PIN 4-6 digit yang di-hash SHA-256 + salt.
    /// </summary>
    public static class VaultManager
    {
        private static byte[] GetLocalBindingKey()
        {
            string id = $"{Environment.UserName}@{Environment.MachineName}::DepupOfficeVault_v2.2.0";
            byte[] salt = SHA256.HashData(Encoding.UTF8.GetBytes("DepupOffice_LocalKeyStore_Salt"));
            return Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(id), salt, 10000, HashAlgorithmName.SHA256, 32);
        }

        private static byte[] GetOrCreateMasterKey()
        {
            byte[] wrapKey = GetLocalBindingKey();
            if (!string.IsNullOrEmpty(AppStorage.Config.VaultMasterKeyProtectedBase64))
            {
                try
                {
                    byte[] blob = Convert.FromBase64String(AppStorage.Config.VaultMasterKeyProtectedBase64);
                    if (blob.Length == 12 + 16 + 32)
                    {
                        byte[] nonce = blob.AsSpan(0, 12).ToArray();
                        byte[] tag = blob.AsSpan(12, 16).ToArray();
                        byte[] cipher = blob.AsSpan(28, 32).ToArray();
                        byte[] plainKey = new byte[32];
                        using var gcm = new AesGcm(wrapKey, 16);
                        gcm.Decrypt(nonce, cipher, tag, plainKey);
                        return plainKey;
                    }
                }
                catch { }
            }

            byte[] key = new byte[32]; // 256-bit AES key
            RandomNumberGenerator.Fill(key);
            try
            {
                byte[] nonce = new byte[12];
                RandomNumberGenerator.Fill(nonce);
                byte[] cipher = new byte[32];
                byte[] tag = new byte[16];
                using (var gcm = new AesGcm(wrapKey, 16))
                {
                    gcm.Encrypt(nonce, key, cipher, tag);
                }
                byte[] blob = new byte[12 + 16 + 32];
                Buffer.BlockCopy(nonce, 0, blob, 0, 12);
                Buffer.BlockCopy(tag, 0, blob, 12, 16);
                Buffer.BlockCopy(cipher, 0, blob, 28, 32);
                AppStorage.Config.VaultMasterKeyProtectedBase64 = Convert.ToBase64String(blob);
                AppStorage.SaveConfig();
            }
            catch { }
            return key;
        }

        private static string Hex(byte[] bytes)
            => string.Concat(bytes.Select(b => b.ToString("x2")));

        private static string HashPin(string salt, string pin)
        {
            byte[] input = Encoding.UTF8.GetBytes(salt + pin);
            byte[] digest = SHA256.HashData(input);
            return Hex(digest);
        }

        public static bool HasPin()
            => !string.IsNullOrEmpty(AppStorage.Config.VaultPinHash);

        public static void SetPin(string pin)
        {
            byte[] saltBytes = new byte[16];
            RandomNumberGenerator.Fill(saltBytes);
            string salt = Hex(saltBytes);
            AppStorage.Config.VaultSalt = salt;
            AppStorage.Config.VaultPinHash = HashPin(salt, pin);
            GetOrCreateMasterKey();
            AppStorage.SaveConfig();
        }

        public static bool CheckPin(string pin)
        {
            string? salt = AppStorage.Config.VaultSalt;
            string? hash = AppStorage.Config.VaultPinHash;
            if (string.IsNullOrEmpty(salt) || string.IsNullOrEmpty(hash)) return false;
            return string.Equals(HashPin(salt, pin), hash, StringComparison.OrdinalIgnoreCase);
        }

        public static List<FileInfo> ListEncryptedFiles()
        {
            var d = new DirectoryInfo(AppStorage.VaultDir);
            if (!d.Exists) return new List<FileInfo>();
            return d.GetFiles("*.enc").OrderByDescending(f => f.LastWriteTimeUtc).ToList();
        }

        public static string DisplayName(FileInfo f)
            => f.Name.EndsWith(".enc", StringComparison.OrdinalIgnoreCase)
                ? f.Name.Substring(0, f.Name.Length - 4)
                : f.Name;

        public static string EncryptFile(string sourceFilePath)
        {
            string origName = Path.GetFileName(sourceFilePath).Replace("/", "_").Replace("\\", "_");
            string dstPath = Path.Combine(AppStorage.VaultDir, $"{origName}.enc");
            if (File.Exists(dstPath))
                dstPath = Path.Combine(AppStorage.VaultDir, $"{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}_{origName}.enc");

            byte[] plain = File.ReadAllBytes(sourceFilePath);
            byte[] key = GetOrCreateMasterKey();
            byte[] nonce = new byte[12]; // 96-bit GCM IV
            RandomNumberGenerator.Fill(nonce);
            byte[] cipher = new byte[plain.Length];
            byte[] tag = new byte[16]; // 128-bit GCM auth tag

            using (var aesGcm = new AesGcm(key, 16))
            {
                aesGcm.Encrypt(nonce, plain, cipher, tag);
            }

            using var fs = new FileStream(dstPath, FileMode.Create, FileAccess.Write, FileShare.None);
            fs.WriteByte((byte)nonce.Length);
            fs.Write(nonce, 0, nonce.Length);
            fs.Write(tag, 0, tag.Length);
            fs.Write(cipher, 0, cipher.Length);
            return dstPath;
        }

        public static void DecryptFile(string encFilePath, string outFilePath)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(outFilePath) ?? AppStorage.AppDataDir);
            using var fs = new FileStream(encFilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            int ivLen = fs.ReadByte();
            if (ivLen <= 0 || ivLen > 32)
                throw new InvalidDataException("Format berkas Vault tidak valid.");

            byte[] nonce = new byte[ivLen];
            fs.ReadExactly(nonce, 0, ivLen);
            byte[] tag = new byte[16];
            fs.ReadExactly(tag, 0, 16);
            byte[] cipher = new byte[fs.Length - 1 - ivLen - 16];
            fs.ReadExactly(cipher, 0, cipher.Length);

            byte[] plain = new byte[cipher.Length];
            byte[] key = GetOrCreateMasterKey();
            using (var aesGcm = new AesGcm(key, 16))
            {
                aesGcm.Decrypt(nonce, cipher, tag, plain);
            }
            File.WriteAllBytes(outFilePath, plain);
        }
    }
}
