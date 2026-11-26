using DepupOfficeDesktop.Core;
using Xunit;

namespace DepupOfficeDesktop.Tests;

/// <summary>Port NotesTest.kt (Android): format JSON catatan harus sama agar bisa dipindah antarperangkat.</summary>
public sealed class NotesTests
{
    private static Note Sample()
    {
        var n = new Note(42, 1000) { Title = "Belanja", Checklist = true, Color = 2, Pinned = true, Updated = 2000 };
        n.Items.AddRange(new[] { new NoteItem("Susu", true), new NoteItem("Roti"), new NoteItem("  ") });
        return n;
    }

    [Fact]
    public void CodecRoundTrip()
    {
        var text = new Note(7) { Body = "Halo\n\"kutip\" & ✓ emoji 😀" };
        var back = NoteCodec.Decode(NoteCodec.Encode(new[] { Sample(), text }));
        Assert.Equal(2, back.Count);
        var a = back[0];
        Assert.Equal(42L, a.Id); Assert.Equal("Belanja", a.Title); Assert.True(a.Checklist);
        Assert.Equal(new[] { ("Susu", true), ("Roti", false), ("  ", false) }, a.Items.Select(i => (i.Text, i.Done)));
        Assert.Equal(2, a.Color); Assert.True(a.Pinned); Assert.Equal(1000L, a.Created); Assert.Equal(2000L, a.Updated);
        Assert.Equal("Halo\n\"kutip\" & ✓ emoji 😀", back[1].Body);
        Assert.False(back[1].Checklist);
    }

    [Fact]
    public void DecodeReadsAndroidJsonSkipsBrokenEntriesAndClampsColor()
    {
        var l = NoteCodec.Decode("""{"v":1,"notes":[{"t":"tanpa id"},{"id":5,"k":99,"c":true,"i":[{"x":"a","d":true}]}]}""");
        Assert.Single(l);
        Assert.Equal(5L, l[0].Id);
        Assert.Equal(NoteColors.All.Length - 1, l[0].Color);
        Assert.Equal(("a", true), (l[0].Items[0].Text, l[0].Items[0].Done));
        Assert.Empty(NoteCodec.Decode("""{"v":1}"""));
    }

    [Theory]
    [InlineData("- [x] Susu", "Susu", true)]
    [InlineData("[X] Roti", "Roti", true)]
    [InlineData("* [ ] Telur", "Telur", false)]
    [InlineData("• Kopi", "Kopi", false)]
    [InlineData("  Teh manis  ", "Teh manis", false)]
    [InlineData("-5 derajat", "-5 derajat", false)]
    public void ParseLineRecognisesBoxesAndBullets(string line, string text, bool done)
    {
        var i = Note.ParseLine(line);
        Assert.Equal((text, done), (i.Text, i.Done));
    }

    [Fact]
    public void TextChecklistConversionAndPlainText()
    {
        var n = new Note(1) { Body = "- [x] Susu\n\n• Roti\nTelur" };
        n.ToChecklist();
        Assert.True(n.Checklist); Assert.Equal("", n.Body);
        Assert.Equal(new[] { ("Susu", true), ("Roti", false), ("Telur", false) }, n.Items.Select(i => (i.Text, i.Done)));
        n.ToText();
        Assert.False(n.Checklist); Assert.Empty(n.Items); Assert.Equal("Susu\nRoti\nTelur", n.Body);

        var s = Sample();
        Assert.Equal((1, 2), s.Progress());
        Assert.False(s.Blank);
        Assert.Equal("Belanja\n\n- [x] Susu\n- [ ] Roti", s.PlainText());
        Assert.True(new Note(4) { Body = " \n " }.Blank);
    }

    [Fact]
    public void HeadlineAndSummary()
    {
        var n = Sample();
        Assert.Equal("Belanja", Notes.Headline(n));
        Assert.Equal("1/2 selesai · Roti", Notes.Summary(n));
        var text = new Note(11) { Body = "Baris pertama\nbaris kedua\n\nketiga" };
        Assert.Equal("Baris pertama", Notes.Headline(text));
        Assert.Equal("baris kedua ketiga", Notes.Summary(text));
        Assert.Equal("Catatan kosong", Notes.Headline(new Note(12)));
        Assert.True(Notes.Matches(text, "KEDUA"));
        Assert.False(Notes.Matches(text, "keempat"));
    }

    [Fact]
    public void StoreSavesSortsAndDeletes()
    {
        var path = Path.Combine(Path.GetTempPath(), $"notes_{Guid.NewGuid():N}.json");
        try
        {
            Notes.UseFileForTests(path);
            var a = new Note(Notes.NewId()) { Title = "A" };
            Notes.Save(a);
            var b = new Note(Notes.NewId()) { Title = "B", Pinned = true };
            Notes.Save(b);
            Assert.Equal(new[] { "B", "A" }, Notes.All().Select(n => n.Title));
            Notes.UseFileForTests(path);   // muat ulang dari disk
            Assert.Equal(2, Notes.Count());
            Notes.Delete(b.Id);
            Assert.Equal(new[] { "A" }, Notes.All().Select(n => n.Title));
        }
        finally { File.Delete(path); Notes.UseFileForTests(null); }
    }
}
