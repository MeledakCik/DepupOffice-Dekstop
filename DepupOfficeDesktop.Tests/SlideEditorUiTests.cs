using System.Drawing;
using System.Drawing.Imaging;
using System.Reflection;
using System.Windows.Forms;
using DepupOfficeDesktop.Core;
using DepupOfficeDesktop.Forms;
using Xunit;

namespace DepupOfficeDesktop.Tests;

/// <summary>
/// Uji jendela Editor Presentasi yang benar-benar dibuka: klik, seret, ketik, pintasan, urungkan, simpan, buka lagi.
/// Potret layar disimpan bila DEPUP_SHOTS diisi.
/// </summary>
public sealed class SlideEditorUiTests
{
    private const BindingFlags Any = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;

    private static T Get<T>(object o, string field) => (T)o.GetType().GetField(field, Any)!.GetValue(o)!;
    private static void Set(object o, string field, object? v) => o.GetType().GetField(field, Any)!.SetValue(o, v);
    private static object? Call(object o, string method, params object?[] args)
    {
        var m = o.GetType().GetMethods(Any).First(x => x.Name == method && x.GetParameters().Length == args.Length);
        return m.Invoke(o, args);
    }

    private static void Pump(int n = 10) { for (int i = 0; i < n; i++) { Application.DoEvents(); Thread.Sleep(10); } }

    private static void RunSta(Action body)
    {
        Exception? err = null;
        var t = new Thread(() =>
        {
            try
            {
                Application.EnableVisualStyles();
                AppStorage.Initialize();
                Theme.Init();
                body();
            }
            catch (Exception ex) { err = ex; }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        if (err is TargetInvocationException { InnerException: { } inner }) throw inner;
        if (err != null) throw err;
    }

    private static void CloseClean(Form f) { Set(f, "_dirty", false); f.Close(); f.Dispose(); }

    private static void Shot(Form f, string name)
    {
        if (Environment.GetEnvironmentVariable("DEPUP_SHOTS") is not { Length: > 0 } dir) return;
        Directory.CreateDirectory(dir);
        Pump(20);
        using var bmp = new Bitmap(f.Width, f.Height);
        f.DrawToBitmap(bmp, new Rectangle(0, 0, f.Width, f.Height));
        bmp.Save(Path.Combine(dir, name), ImageFormat.Png);
    }

    [Fact]
    public void EditorWorkflowEndToEnd()
    {
        string dir = Directory.CreateTempSubdirectory("depup-slide-ui-").FullName;
        try
        {
            RunSta(() =>
            {
                Theme.SetMode(Theme.MODE_LIGHT);
                var f = new SlideEditorForm(template: SlideDeck.TEMPLATE_SCHOOL) { StartPosition = FormStartPosition.Manual, Location = new Point(0, 0), Size = new Size(1280, 820) };
                f.Show();
                Pump(30);
                var deck = Get<SlideDeck>(f, "_deck");
                Assert.Equal(6, deck.Slides.Count);
                var strip = Get<FlowLayoutPanel>(f, "_strip");
                Assert.Equal(6, strip.Controls.Count);
                Shot(f, "presentasi_terang.png");

                // --- pindah slide lewat daftar kiri
                Call(f, "GoTo", 1);
                Assert.Equal(1, Get<int>(f, "_cur"));

                // --- klik elemen judul di kanvas lalu seret 100 satuan ke kanan
                var canvas = Get<Control>(f, "_canvas");
                var slide = deck.Slides[1];
                var title = slide.Items[0];
                var rect = (RectangleF)Call(f, "ToScreen", title.Box)!;
                var start = new Point((int)(rect.X + rect.Width / 2), (int)(rect.Y + rect.Height / 2));
                float k = (float)f.GetType().GetProperty("CanvasScale", Any)!.GetValue(f)!;
                var end = new Point(start.X + (int)Math.Round(100 * k), start.Y);
                float x0 = title.Box.X;
                Call(f, "CanvasDown", canvas, new MouseEventArgs(MouseButtons.Left, 1, start.X, start.Y, 0));
                Assert.Equal(0, Get<int>(f, "_sel"));
                Call(f, "CanvasMove", canvas, new MouseEventArgs(MouseButtons.Left, 0, (start.X + end.X) / 2, start.Y, 0));
                Call(f, "CanvasMove", canvas, new MouseEventArgs(MouseButtons.Left, 0, end.X, end.Y, 0));
                Call(f, "CanvasUp", canvas, new MouseEventArgs(MouseButtons.Left, 1, end.X, end.Y, 0));
                Assert.InRange(title.Box.X - x0, 97f, 103f);
                Assert.Single(Get<System.Collections.IList>(f, "_undo"));

                // --- urungkan / ulangi seret
                Call(f, "Undo");
                deck = Get<SlideDeck>(f, "_deck");
                Assert.Equal(x0, deck.Slides[1].Items[0].Box.X, 1);
                Call(f, "Redo");
                deck = Get<SlideDeck>(f, "_deck");
                Assert.InRange(deck.Slides[1].Items[0].Box.X - x0, 97f, 103f);

                // --- ketik saat elemen terpilih = mulai menyunting, Esc = selesai
                Call(f, "Select", 0);
                Call(f, "CanvasKeyPress", canvas, new KeyPressEventArgs('P'));
                var editor = Get<TextBox>(f, "_editor");
                Assert.NotNull(editor);
                editor.AppendText("embuka\r\nBaris kedua");
                Pump();
                Call(f, "CommitEdit");
                deck = Get<SlideDeck>(f, "_deck");
                Assert.Equal("Pembuka\nBaris kedua", deck.Slides[1].Items[0].Text);
                Assert.Null(Get<TextBox?>(f, "_editor"));
                Assert.True(deck.Slides[1].Items[0].Box.Height >= SlideDeck.TextHeight(deck.Slides[1].Items[0]) - 0.5f, "kotak tumbuh mengikuti teks");

                // --- format: tebal + ukuran
                Call(f, "Format", (Action<SlideItem>)(i => i.Size = 54), false);
                deck = Get<SlideDeck>(f, "_deck");
                Assert.Equal(54f, deck.Slides[1].Items[0].Size);

                // --- sisipkan bentuk, gambar dari papan klip internal, duplikat, susun, hapus
                Call(f, "InsertShape", SlideShape.Ellipse);
                deck = Get<SlideDeck>(f, "_deck");
                int n = deck.Slides[1].Items.Count;
                Assert.Equal(SlideShape.Ellipse, deck.Slides[1].Items[^1].Shape);
                Call(f, "DuplicateSelected");
                deck = Get<SlideDeck>(f, "_deck");
                Assert.Equal(n + 1, deck.Slides[1].Items.Count);
                Call(f, "Arrange", int.MinValue);
                Assert.Equal(0, Get<int>(f, "_sel"));
                Call(f, "DeleteSelected");
                deck = Get<SlideDeck>(f, "_deck");
                Assert.Equal(n, deck.Slides[1].Items.Count);

                // --- kotak teks baru yang ditinggal kosong dibuang
                int before = deck.Slides[1].Items.Count;
                Call(f, "InsertText");
                Assert.NotNull(Get<TextBox?>(f, "_editor"));
                Call(f, "CommitEdit");
                deck = Get<SlideDeck>(f, "_deck");
                Assert.Equal(before, deck.Slides[1].Items.Count);

                // --- slide: tambah, duplikat, pindah, ganti tata letak (teks ikut), hapus
                Call(f, "AddSlide", SlideLayout.Blank);
                deck = Get<SlideDeck>(f, "_deck");
                Assert.Equal(7, deck.Slides.Count);
                Assert.Equal(2, Get<int>(f, "_cur"));
                Assert.Equal(7, strip.Controls.Count);
                Call(f, "DeleteSlide");
                Call(f, "GoTo", 1);
                Call(f, "ApplyLayout", SlideLayout.TwoContent);
                deck = Get<SlideDeck>(f, "_deck");
                var relaid = deck.Slides[1];
                Assert.Equal("Pembuka\nBaris kedua", relaid.Items.First(i => i.Role == "title").Text);
                Assert.Contains(relaid.Items, i => i.Role == "body" && i.Text.Contains("Latar belakang"));
                Call(f, "MoveSlide", 1);
                Assert.Equal(2, Get<int>(f, "_cur"));

                // --- tema
                Call(f, "Commit", (Action)(() => Get<SlideDeck>(f, "_deck").ApplyTheme(1)), true);
                deck = Get<SlideDeck>(f, "_deck");
                Assert.Equal(DeckTheme.All[1].Bg.ToArgb(), deck.Slides[0].Background.ToArgb());
                Shot(f, "presentasi_tema_malam.png");

                // --- simpan, lalu buka ulang di editor baru
                string path = Path.Combine(dir, "uji.pptx");
                Assert.True((bool)Call(f, "WriteTo", path)!);
                Assert.False(Get<bool>(f, "_dirty"));
                CloseClean(f);

                var g = new SlideEditorForm(path) { Size = new Size(1100, 760) };
                g.Show();
                Pump(20);
                var back = Get<SlideDeck>(g, "_deck");
                Assert.Equal(6, back.Slides.Count);
                Assert.Equal(1, back.ThemeIndex);
                Assert.Equal("Pembuka\nBaris kedua", back.Slides[2].Items.First(i => i.Role == "title").Text);
                Assert.Equal(54f, back.Slides[2].Items.First(i => i.Role == "title").Size);
                Assert.False(back.Foreign);
                CloseClean(g);

                // --- PDF dari model yang sama
                string pdf = Path.Combine(dir, "uji.pdf");
                back.ExportPdf(pdf);
                Assert.Equal(6, PdfToolsX.OpenPdf(pdf).Pages().Count);
            });
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void EditorRendersInDarkThemeAndNarrowWindow()
    {
        RunSta(() =>
        {
            Theme.SetMode(Theme.MODE_DARK);
            try
            {
                var f = new SlideEditorForm(template: SlideDeck.TEMPLATE_PITCH) { StartPosition = FormStartPosition.Manual, Location = new Point(0, 0), Size = new Size(900, 640) };
                f.Show();
                Pump(30);
                Call(f, "GoTo", 4);
                Call(f, "Select", 1);
                Shot(f, "presentasi_gelap_sempit.png");
                // pita membungkus: kanvas tetap mendapat ruang yang layak
                var canvas = Get<Control>(f, "_canvas");
                Assert.True(canvas.Height > 250, $"tinggi kanvas {canvas.Height}");
                var r = (RectangleF)Call(f, "SlideRect")!;
                Assert.True(r.Width > 300);
                CloseClean(f);
            }
            finally { Theme.SetMode(Theme.MODE_LIGHT); }
        });
    }
}
