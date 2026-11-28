using System;
using System.Windows.Forms;
using DepupOfficeDesktop.Core;
using DepupOfficeDesktop.Forms;

namespace DepupOfficeDesktop
{
    internal static class Program
    {
        [STAThread]
        static void Main(string[] args)
        {
            ApplicationConfiguration.Initialize();
            Theme.InstallAutoTheme();          // semua jendela & dialog ikut tema Depup

            // Galat tak terduga di satu fitur tidak boleh menutup seluruh aplikasi: tampilkan pesan, lanjutkan.
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += (s, e) =>
            {
                try
                {
                    MessageBox.Show($"Terjadi kesalahan dan proses dibatalkan:\n\n{e.Exception.Message}",
                        "DEPUP OFFICE", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
                catch { }
            };
            AppStorage.Initialize();
            Theme.Init();                      // tema terang / gelap (Ikuti Windows bawaan, seperti Android)
            ProtectedDoc.CleanupLeftovers();   // salinan terdekripsi yang tertinggal (aplikasi ditutup paksa)
            // semua jendela (termasuk dialog) memakai ikon logo DepUp, bukan ikon bawaan WinForms
            Application.Idle += (_, _) =>
            {
                if (Theme.AppIcon is not { } icon) return;
                foreach (Form f in Application.OpenForms)
                    if (f.ShowIcon && !ReferenceEquals(f.Icon, icon)) f.Icon = icon;
            };

            string? initialFile = null;
            if (args.Length > 0)
            {
                if (!File.Exists(args[0]))
                    MessageBox.Show($"Berkas tidak ditemukan:\n{args[0]}", "DEPUP OFFICE", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                else if (!AppStorage.IsSupportedDocument(args[0]))
                    MessageBox.Show(AppStorage.UnsupportedDocumentMessage(args[0]), "DEPUP OFFICE", MessageBoxButtons.OK, MessageBoxIcon.Information);
                else
                    initialFile = args[0];
            }
            Application.Run(new MainForm(initialFile));
        }
    }
}
