using System.Text.RegularExpressions;

namespace DepupOfficeDesktop.Core
{
    /// <summary>Parser daftar halaman seperti "1-3, 5, 8-6" (berbasis 1) → indeks berbasis 0 sesuai urutan.</summary>
    public static class PageRange
    {
        public static List<int> Parse(string spec, int pageCount)
        {
            var result = new List<int>();
            if (pageCount <= 0) return result;
            if (string.IsNullOrWhiteSpace(spec)) throw new FormatException("Daftar halaman kosong.");
            foreach (var raw in spec.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string part = raw.Trim().ToLowerInvariant().Replace("akhir", pageCount.ToString()).Replace("end", pageCount.ToString());
                if (part.Length == 0) continue;
                var m = Regex.Match(part, @"^(\d+)\s*[-–]\s*(\d+)$");
                if (m.Success)
                {
                    int a = int.Parse(m.Groups[1].Value), b = int.Parse(m.Groups[2].Value);
                    Check(a, pageCount); Check(b, pageCount);
                    int step = a <= b ? 1 : -1;
                    for (int p = a; p != b + step; p += step) result.Add(p - 1);
                }
                else if (int.TryParse(part, out int single))
                {
                    Check(single, pageCount);
                    result.Add(single - 1);
                }
                else throw new FormatException($"Bagian \"{raw.Trim()}\" tidak dikenali. Contoh: 1-3, 5, 8");
            }
            if (result.Count == 0) throw new FormatException("Tidak ada halaman yang dipilih.");
            return result;
        }

        private static void Check(int page, int count)
        {
            if (page < 1 || page > count) throw new FormatException($"Halaman {page} tidak ada (dokumen berisi {count} halaman).");
        }

        public static string All(int pageCount) => pageCount <= 1 ? "1" : $"1-{pageCount}";
    }
}
