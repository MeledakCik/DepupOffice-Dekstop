using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace DepupOfficeDesktop.Core
{
    /// <summary>Bagian murni lembar Cetak DepUp — port <c>PrintPages.kt</c>: baca/tulis nomor halaman "1-3, 5".</summary>
    public static class PrintPages
    {
        private static readonly Regex Dash = new(@"\s*[-–—]\s*");
        private static readonly Regex Sep = new(@"[,;\s]+");
        private static readonly Regex Part = new(@"^(\d+)?(-)?(\d+)?$");

        /// <summary>
        /// "1-3, 5, 8-" → halaman 0-based, urut &amp; tanpa ganda. null bila kosong, bukan angka, atau di luar 1..n.
        /// "8-" = sampai halaman terakhir, "-3" = dari halaman pertama.
        /// </summary>
        public static List<int>? Parse(string t, int n)
        {
            var output = new SortedSet<int>();
            var norm = Dash.Replace(t, "-");
            foreach (var p in Sep.Split(norm).Where(x => x.Length > 0))
            {
                var m = Part.Match(p);
                if (!m.Success) return null;
                int? a = int.TryParse(m.Groups[1].Value, out var av) ? av : null;
                bool dash = m.Groups[2].Value.Length > 0;
                int? b = int.TryParse(m.Groups[3].Value, out var bv) ? bv : null;
                int from, to;
                if (!dash && a != null) { from = to = a.Value; }
                else if (dash && a != null && b != null) { from = a.Value; to = b.Value; }
                else if (dash && a != null) { from = a.Value; to = n; }
                else if (dash && b != null) { from = 1; to = b.Value; }
                else return null;
                if (from < 1 || from > n || to < 1 || to > n) return null;
                for (int k = Math.Min(from, to); k <= Math.Max(from, to); k++) output.Add(k - 1);
            }
            return output.Count == 0 ? null : output.ToList();
        }

        /// <summary>Teks ringkas "1-3, 5" dari halaman 0-based.</summary>
        public static string Text(IEnumerable<int> pages)
        {
            var s = pages.Distinct().OrderBy(x => x).ToList();
            var sb = new StringBuilder();
            int i = 0;
            while (i < s.Count)
            {
                int j = i;
                while (j + 1 < s.Count && s[j + 1] == s[j] + 1) j++;
                if (sb.Length > 0) sb.Append(", ");
                sb.Append(s[i] + 1);
                if (j > i) sb.Append('-').Append(s[j] + 1);
                i = j + 1;
            }
            return sb.ToString();
        }

        /// <summary>Jumlah lembar halaman tugas cetak (termasuk halaman kosong pengganjal bolak-balik).</summary>
        public static int JobPages(int pages, int copies, bool duplex) =>
            pages * copies + (duplex && copies > 1 && pages % 2 == 1 ? copies - 1 : 0);

        /// <summary>
        /// Urutan tugas cetak tersusun (1-2-3, 1-2-3). -1 = halaman kosong pengganjal: bolak-balik + jumlah halaman ganjil →
        /// tiap salinan mulai di lembar baru.
        /// </summary>
        public static List<int> JobOrder(IReadOnlyList<int> pages, int copies, bool duplex)
        {
            var order = new List<int>();
            bool pad = duplex && copies > 1 && pages.Count % 2 == 1;
            for (int c = 0; c < copies; c++)
            {
                order.AddRange(pages);
                if (pad && c < copies - 1) order.Add(-1);
            }
            return order;
        }
    }
}
