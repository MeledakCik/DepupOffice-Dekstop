using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace DepupOfficeDesktop.Core
{
    // =====================================================================================================
    // Catatan — port Notes.kt / NoteViews.kt (Android 4.3). Teks biasa ATAU daftar centang, warna kertas tempel,
    // sematan. Disimpan offline di %LocalAppData%\DepupOfficeDesktop\notes.json dengan format JSON v1 yang sama
    // dengan Android (berkas bisa dipindah antarperangkat).
    // =====================================================================================================

    /// <summary>Satu baris daftar centang.</summary>
    public sealed class NoteItem
    {
        public string Text { get; set; }
        public bool Done { get; set; }
        public NoteItem(string text, bool done = false) { Text = text; Done = done; }
        public NoteItem Copy() => new(Text, Done);
    }

    public sealed class Note
    {
        public long Id { get; }
        public string Title { get; set; } = "";
        public string Body { get; set; } = "";
        public List<NoteItem> Items { get; } = new();
        public bool Checklist { get; set; }
        /// <summary>Indeks <see cref="NoteColors.All"/>; 0 = bawaan.</summary>
        public int Color { get; set; }
        public bool Pinned { get; set; }
        public long Created { get; }
        public long Updated { get; set; }

        public Note(long id, long? created = null)
        {
            Id = id;
            Created = created ?? Now();
            Updated = Created;
        }

        internal static long Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        public Note Copy()
        {
            var n = new Note(Id, Created) { Title = Title, Body = Body, Checklist = Checklist, Color = Color, Pinned = Pinned, Updated = Updated };
            n.Items.AddRange(Items.Select(i => i.Copy()));
            return n;
        }

        /// <summary>Tanpa judul dan tanpa isi (item kosong tidak dihitung).</summary>
        public bool Blank => string.IsNullOrWhiteSpace(Title) && (Checklist ? Items.All(i => string.IsNullOrWhiteSpace(i.Text)) : string.IsNullOrWhiteSpace(Body));

        /// <summary>Item berisi: (sudah dicentang, semuanya).</summary>
        public (int done, int total) Progress()
        {
            var f = Items.Where(i => !string.IsNullOrWhiteSpace(i.Text)).ToList();
            return (f.Count(i => i.Done), f.Count);
        }

        /// <summary>Teks → daftar centang: tiap baris jadi satu item (poin "- ", "• ", "[x] " dikenali).</summary>
        public void ToChecklist()
        {
            if (Checklist) return;
            Items.Clear();
            foreach (var l in Body.Split('\n').Select(x => x.Trim()).Where(x => x.Length > 0)) Items.Add(ParseLine(l));
            if (Items.Count == 0) Items.Add(new NoteItem(""));
            Body = "";
            Checklist = true;
        }

        /// <summary>Daftar centang → teks biasa: satu baris per item (tanda centang dilepas).</summary>
        public void ToText()
        {
            if (!Checklist) return;
            Body = string.Join("\n", Items.Where(i => !string.IsNullOrWhiteSpace(i.Text)).Select(i => i.Text.Trim()));
            Items.Clear();
            Checklist = false;
        }

        /// <summary>Teks polos untuk dibagikan/disalin; item ditulis "- [x] …" / "- [ ] …".</summary>
        public string PlainText()
        {
            var sb = new StringBuilder();
            if (!string.IsNullOrWhiteSpace(Title)) sb.Append(Title.Trim());
            string content = Checklist
                ? string.Join("\n", Items.Where(i => !string.IsNullOrWhiteSpace(i.Text)).Select(i => (i.Done ? "- [x] " : "- [ ] ") + i.Text.Trim()))
                : Body.Trim();
            if (content.Length > 0) { if (sb.Length > 0) sb.Append("\n\n"); sb.Append(content); }
            return sb.ToString();
        }

        private static readonly Regex Box = new(@"^[-*•]?\s*\[([ xX])]\s*");
        private static readonly Regex Bullet = new(@"^[-*•]\s+");

        /// <summary>"- [x] Susu" → (Susu, dicentang); "• Roti" → (Roti, belum).</summary>
        public static NoteItem ParseLine(string line)
        {
            var t = line.Trim();
            var m = Box.Match(t);
            if (m.Success) return new NoteItem(t[(m.Index + m.Length)..].Trim(), !string.IsNullOrWhiteSpace(m.Groups[1].Value));
            return new NoteItem(Bullet.Replace(t, "", 1).Trim());
        }
    }

    /// <summary>Warna catatan seperti kertas tempel (pasangan terang/gelap, nilai sama dengan Android). 0 = bawaan.</summary>
    public static class NoteColors
    {
        public sealed record Tone(string Name, Color Light, Color Dark);

        public static readonly Tone[] All =
        {
            new("Bawaan", ColorTranslator.FromHtml("#FFFFFF"), ColorTranslator.FromHtml("#1B1B1A")),
            new("Kuning", ColorTranslator.FromHtml("#FFF0B3"), ColorTranslator.FromHtml("#4A3E10")),
            new("Oranye", ColorTranslator.FromHtml("#FFDDC4"), ColorTranslator.FromHtml("#4E2D16")),
            new("Merah muda", ColorTranslator.FromHtml("#FAD5E0"), ColorTranslator.FromHtml("#4B2131")),
            new("Ungu", ColorTranslator.FromHtml("#E5DBFA"), ColorTranslator.FromHtml("#34294F")),
            new("Biru", ColorTranslator.FromHtml("#D4E4FC"), ColorTranslator.FromHtml("#1E3350")),
            new("Hijau", ColorTranslator.FromHtml("#D6F0DA"), ColorTranslator.FromHtml("#1E3B27")),
        };

        public static Tone Of(int i) => i >= 0 && i < All.Length ? All[i] : All[0];

        /// <summary>Isian kartu catatan di tema saat ini (bawaan = permukaan kartu).</summary>
        public static Color Fill(int color) => color <= 0 ? Theme.SURFACE : (Theme.Dark ? Of(color).Dark : Of(color).Light);

        /// <summary>Latar halaman editor (bawaan = latar halaman aplikasi).</summary>
        public static Color Page(int color) => color <= 0 ? Theme.BG : Fill(color);

        /// <summary>Teks sekunder yang tetap terbaca di atas warna catatan (≥ 4,5:1).</summary>
        public static Color Muted(int color) => color <= 0 ? Theme.MUTED : ColorTranslator.FromHtml(Theme.Dark ? "#B3B0AA" : "#5C5953");

        public static Color Text(int color) => color <= 0 ? Theme.TEXT : ColorTranslator.FromHtml(Theme.Dark ? "#F2F1EE" : "#111111");
    }

    /// <summary>Simpan/baca catatan sebagai JSON v1 (identik Android NoteCodec).</summary>
    public static class NoteCodec
    {
        public static string Encode(IEnumerable<Note> list)
        {
            var a = new JsonArray();
            foreach (var n in list) a.Add(ToJson(n));
            return new JsonObject { ["v"] = 1, ["notes"] = a }.ToJsonString();
        }

        public static JsonObject ToJson(Note n)
        {
            var items = new JsonArray();
            foreach (var i in n.Items) items.Add(new JsonObject { ["x"] = i.Text, ["d"] = i.Done });
            return new JsonObject
            {
                ["id"] = n.Id, ["t"] = n.Title, ["b"] = n.Body, ["c"] = n.Checklist, ["i"] = items,
                ["k"] = n.Color, ["p"] = n.Pinned, ["cr"] = n.Created, ["up"] = n.Updated
            };
        }

        /// <summary>Entri rusak dilewati; berkas rusak total → pengecualian (pemanggil menyimpan cadangannya).</summary>
        public static List<Note> Decode(string s)
        {
            var output = new List<Note>();
            if (JsonNode.Parse(s) is not JsonObject root || root["notes"] is not JsonArray a) return output;
            foreach (var node in a)
            {
                if (node is not JsonObject o || o["id"] == null) continue;
                try
                {
                    long created = Long(o["cr"]) ?? Note.Now();
                    var n = new Note(Long(o["id"])!.Value, created)
                    {
                        Title = Str(o["t"]), Body = Str(o["b"]), Checklist = Bool(o["c"]),
                        Color = Math.Clamp((int)(Long(o["k"]) ?? 0), 0, NoteColors.All.Length - 1),
                        Pinned = Bool(o["p"]), Updated = Long(o["up"]) ?? created
                    };
                    if (o["i"] is JsonArray ia)
                        foreach (var it in ia.OfType<JsonObject>()) n.Items.Add(new NoteItem(Str(it["x"]), Bool(it["d"])));
                    output.Add(n);
                }
                catch { }
            }
            return output;
        }

        private static string Str(JsonNode? n) { try { return n?.GetValue<string>() ?? ""; } catch { return n?.ToString() ?? ""; } }
        private static bool Bool(JsonNode? n) { try { return n?.GetValue<bool>() ?? false; } catch { return false; } }
        private static long? Long(JsonNode? n)
        {
            if (n == null) return null;
            try { return n.GetValue<long>(); } catch { }
            try { return (long)n.GetValue<double>(); } catch { }
            return long.TryParse(n.ToString(), out var v) ? v : null;
        }
    }

    /// <summary>
    /// Penyimpanan catatan: satu berkas JSON lokal (tidak pernah keluar dari PC), ditulis atomik (tmp → replace).
    /// Isi di-cache di memori; perubahan memicu <see cref="Changed"/> agar Beranda/layar Catatan ikut diperbarui.
    /// </summary>
    public static class Notes
    {
        private static readonly object Gate = new();
        private static List<Note>? _cache;
        private static long _lastId;
        private static string? _pathOverride;

        public static event Action? Changed;

        private static string FilePath => _pathOverride ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DepupOfficeDesktop", "notes.json");

        /// <summary>Hanya untuk uji: arahkan penyimpanan ke berkas lain.</summary>
        internal static void UseFileForTests(string? path) { lock (Gate) { _pathOverride = path; _cache = null; } }

        private static List<Note> Loaded()
        {
            if (_cache != null) return _cache;
            var p = FilePath;
            List<Note> l;
            try { l = File.Exists(p) ? NoteCodec.Decode(File.ReadAllText(p, Encoding.UTF8)) : new(); }
            catch
            {
                // berkas rusak: simpan salinannya (tidak dihapus) lalu mulai dari daftar kosong
                try { File.Copy(p, Path.Combine(Path.GetDirectoryName(p)!, $"notes-rusak-{Note.Now()}.json"), true); } catch { }
                l = new();
            }
            _cache = l;
            return l;
        }

        private static void Persist(List<Note> l)
        {
            var p = FilePath;
            Directory.CreateDirectory(Path.GetDirectoryName(p)!);
            var tmp = p + ".tmp";
            File.WriteAllText(tmp, NoteCodec.Encode(l), new UTF8Encoding(false));
            if (File.Exists(p)) File.Replace(tmp, p, null, true); else File.Move(tmp, p);
        }

        private static void Notify() { try { Changed?.Invoke(); } catch { } }

        /// <summary>Semua catatan (salinan): disematkan dulu, lalu yang terakhir diubah.</summary>
        public static List<Note> All()
        {
            lock (Gate) return Loaded().Select(n => n.Copy()).OrderByDescending(n => n.Pinned).ThenByDescending(n => n.Updated).ToList();
        }

        public static Note? Get(long id) { lock (Gate) return Loaded().FirstOrDefault(n => n.Id == id)?.Copy(); }

        public static int Count() { lock (Gate) return Loaded().Count; }

        /// <summary>Id baru yang belum dipakai (berbasis waktu, selalu naik).</summary>
        public static long NewId()
        {
            lock (Gate)
            {
                var l = Loaded();
                long id = Math.Max(Note.Now(), _lastId + 1);
                while (l.Any(n => n.Id == id)) id++;
                _lastId = id;
                return id;
            }
        }

        /// <summary>Simpan (tambah atau timpa). <paramref name="touch"/> = waktu ubah diperbarui. Mengembalikan waktu ubah.</summary>
        public static long Save(Note n, bool touch = true)
        {
            var c = n.Copy();
            if (touch) c.Updated = Note.Now();
            lock (Gate)
            {
                var l = Loaded();
                int i = l.FindIndex(x => x.Id == c.Id);
                if (i >= 0) l[i] = c; else l.Add(c);
                Persist(l);
            }
            Notify();
            return c.Updated;
        }

        public static void Delete(long id)
        {
            lock (Gate)
            {
                var l = Loaded();
                if (l.RemoveAll(n => n.Id == id) > 0) Persist(l);
            }
            Notify();
        }

        /// <summary>Ubah sebagian (sematkan, warna) tanpa membuka editor.</summary>
        public static void Edit(long id, Action<Note> block, bool touch = false)
        {
            var n = Get(id);
            if (n == null) return;
            block(n);
            Save(n, touch);
        }

        // ------------------------------------------------------------------ teks tampilan (NoteViews.kt)

        private static readonly Regex Ws = new(@"\s+");
        private static string Flat(string s) => Ws.Replace(s, " ").Trim();
        private static string Take(string s, int n) => s.Length <= n ? s : s[..n];

        /// <summary>Judul tampilan: judul, atau baris/item pertama bila tanpa judul.</summary>
        public static string Headline(Note n)
        {
            var t = Flat(n.Title);
            if (t.Length > 0) return Take(t, 120);
            string? first = n.Checklist
                ? n.Items.FirstOrDefault(i => !string.IsNullOrWhiteSpace(i.Text))?.Text
                : n.Body.Split('\n').FirstOrDefault(l => !string.IsNullOrWhiteSpace(l));
            return first != null ? Take(Flat(first), 120) : (n.Checklist ? "Daftar centang" : "Catatan kosong");
        }

        /// <summary>Ringkasan pendek di bawah judul. Tidak pernah kosong.</summary>
        public static string Summary(Note n, string? head = null)
        {
            head ??= Headline(n);
            bool titled = !string.IsNullOrWhiteSpace(n.Title);
            string s;
            if (n.Checklist)
            {
                var (done, total) = n.Progress();
                var open = n.Items.Where(i => !string.IsNullOrWhiteSpace(i.Text) && !i.Done).Select(i => Flat(i.Text)).ToList();
                if (!titled && open.Count > 0 && open[0] == head) open.RemoveAt(0);
                var list = string.Join(", ", open.Take(6));
                s = total == 0 ? "" : list.Length == 0 ? (done == total ? $"Semua selesai ({total})" : $"{done}/{total} selesai") : $"{done}/{total} selesai · {list}";
            }
            else
            {
                var lines = n.Body.Split('\n').Select(Flat).Where(l => l.Length > 0);
                s = string.Join(" ", titled ? lines : lines.Skip(1));
            }
            s = Take(s, 160);
            return s.Length > 0 ? s : AppStorage.RelTime(n.Updated);
        }

        /// <summary>"Diubah 5 menit lalu", "Diubah kemarin, 08.15", …</summary>
        public static string Edited(Note n)
        {
            var r = AppStorage.RelTime(n.Updated);
            if (string.IsNullOrEmpty(r)) return "";
            if (r == "Baru saja") return "Diubah baru saja";
            if (r.StartsWith("Kemarin")) return "Diubah kemarin" + r["Kemarin".Length..];
            return "Diubah " + r;
        }

        public static bool Matches(Note n, string q)
        {
            if (string.IsNullOrWhiteSpace(q)) return true;
            var needle = q.Trim();
            return n.Title.Contains(needle, StringComparison.OrdinalIgnoreCase) || n.Body.Contains(needle, StringComparison.OrdinalIgnoreCase) ||
                n.Items.Any(i => i.Text.Contains(needle, StringComparison.OrdinalIgnoreCase));
        }
    }
}
