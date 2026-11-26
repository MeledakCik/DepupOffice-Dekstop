using DepupOfficeDesktop.Core;
using Xunit;

namespace DepupOfficeDesktop.Tests;

/// <summary>Port PrintPagesTest.kt &amp; SheetLayoutTest.kt (Android 4.3): lembar Cetak DepUp &amp; cetak spreadsheet utuh.</summary>
public sealed class PrintLayoutTests
{
    [Fact]
    public void ParsesAndWritesPageRanges()
    {
        Assert.Equal(new[] { 0, 1, 2, 4 }, PrintPages.Parse("1-3, 5", 10));
        Assert.Equal(new[] { 0, 1, 2 }, PrintPages.Parse("1 - 3", 10));
        Assert.Equal(new[] { 0, 1, 2 }, PrintPages.Parse("3-1", 10));
        Assert.Equal(new[] { 7, 8, 9 }, PrintPages.Parse("8-", 10));
        Assert.Equal(new[] { 0, 1, 2 }, PrintPages.Parse("-3", 10));
        Assert.Equal(new[] { 0, 1 }, PrintPages.Parse("2,2 1", 10));
        foreach (var bad in new[] { "", "0", "11", "1-x", "-" }) Assert.Null(PrintPages.Parse(bad, 10));
        Assert.Equal("1-3, 5", PrintPages.Text(new[] { 4, 0, 2, 1 }));
        Assert.Equal("1, 3, 5-6", PrintPages.Text(new[] { 0, 2, 4, 5 }));
        Assert.Equal("", PrintPages.Text(Array.Empty<int>()));
    }

    [Fact]
    public void JobOrderIsCollatedAndPadsOddDuplex()
    {
        Assert.Equal(new[] { 2, 0, 2, 0 }, PrintPages.JobOrder(new[] { 2, 0 }, 2, true));
        Assert.Equal(4, PrintPages.JobPages(2, 2, true));
        Assert.Equal(new[] { 1, -1, 1, -1, 1 }, PrintPages.JobOrder(new[] { 1 }, 3, true));
        Assert.Equal(5, PrintPages.JobPages(1, 3, true));
        Assert.Equal(new[] { 1, 1, 1 }, PrintPages.JobOrder(new[] { 1 }, 3, false));
    }

    private static readonly SheetLayout.Measure M = (t, b) => t.Length * (b ? 5.5f : 5f);

    private static SheetLayout.Grid G(IEnumerable<IEnumerable<string>> rows, ISet<int>? bold = null)
    {
        bold ??= new HashSet<int> { 0 };
        var list = rows.Select(r => r.ToList()).ToList();
        return new SheetLayout.Grid("Lembar", list.Select((r, i) => (IReadOnlyList<SheetLayout.Cell>)r.Select(t => new SheetLayout.Cell(t, bold.Contains(i))).ToList()).ToList());
    }


    [Fact]
    public void SmallSheetFitsOnePortraitPage()
    {
        var g = G(new[] { new[] { "Produk", "Qty", "Harga", "Total" }, new[] { "Kertas A4", "2", "45000", "90000" }, new[] { "TOTAL", "", "", "90000" } });
        var p = Assert.Single(SheetLayout.MakePlan(new[] { g }, M).Pages);
        Assert.False(p.Landscape); Assert.Equal(1f, p.Scale); Assert.True(p.Title);
        Assert.Equal(new[] { 0, 1, 2, 3 }, p.Cols); Assert.Equal(new[] { 0, 1, 2 }, p.Rows);
    }

    [Fact]
    public void LongSheetsBreakIntoPagesAndRepeatTheHeader()
    {
        var rows = new[] { new[] { "No", "Nama", "Nilai" } }.Concat(Enumerable.Range(1, 300).Select(i => new[] { $"{i}", $"Siswa {i}", $"{60 + i % 40}" }));
        var g = G(rows);
        var plan = SheetLayout.MakePlan(new[] { g }, M);
        Assert.True(plan.Pages.Count > 3);
        Assert.Equal(0, plan.Header[0]);
        foreach (var p in plan.Pages.Skip(1)) Assert.Equal(0, p.Rows[0]);
        foreach (var p in plan.Pages)
            Assert.True(p.Rows.Sum(r => plan.RowH[0][r]) <= SheetLayout.A4_H - 2 * SheetLayout.MARGIN - SheetLayout.FOOT_H + 0.01);
        Assert.Equal(new[] { true }.Concat(Enumerable.Repeat(false, plan.Pages.Count - 1)), plan.Pages.Select(p => p.Title));
        var all = plan.Pages.SelectMany(p => p.Rows).Where(r => r != 0).ToList();
        Assert.Equal(Enumerable.Range(1, 300), all);
    }

    [Fact]
    public void WideSheetsGoLandscapeShrinkThenSplitColumns()
    {
        var mid = G(new[] { Enumerable.Repeat(new string('x', 26), 5), Enumerable.Repeat("y", 5) });
        var pm = Assert.Single(SheetLayout.MakePlan(new[] { mid }, M).Pages);
        Assert.True(pm.Landscape); Assert.Equal(1f, pm.Scale);
        var wide = G(new[] { Enumerable.Repeat(new string('x', 26), 6), Enumerable.Repeat("y", 6) });
        var pw = Assert.Single(SheetLayout.MakePlan(new[] { wide }, M).Pages);
        Assert.True(pw.Landscape); Assert.InRange(pw.Scale, 0.75f, 0.99f);
        var rows = new[] { Enumerable.Range(0, 8).Select(i => $"Judul {i + 1} " + new string('x', 40)) }
            .Concat(Enumerable.Range(1, 80).Select(r => Enumerable.Range(0, 8).Select(c => $"r{r} c{c}")));
        var huge = G(rows);
        var plan = SheetLayout.MakePlan(new[] { huge }, M);
        Assert.All(plan.Pages, p => { Assert.True(p.Landscape); Assert.Equal(SheetLayout.MIN_SCALE, p.Scale); });
        Assert.True(plan.Pages[0].Bands > 1);
        var bands = plan.Pages.Select(p => p.Band).ToList();
        Assert.Equal(bands.OrderBy(x => x), bands);
        Assert.Equal(Enumerable.Range(0, 8), plan.Pages.DistinctBy(p => p.Band).SelectMany(p => p.Cols));
    }

    [Fact]
    public void SlightlyWideStaysPortraitAndLongTextWraps()
    {
        var g = G(new[] { Enumerable.Repeat(new string('x', 24), 4), Enumerable.Repeat("y", 4) });
        var p = Assert.Single(SheetLayout.MakePlan(new[] { g }, M).Pages);
        Assert.False(p.Landscape); Assert.InRange(p.Scale, 0.9f, 0.99f);

        var words = string.Join(" ", Enumerable.Range(1, 30).Select(i => $"kata{i}"));
        var t = G(new[] { new[] { "Catatan" }, new[] { words } }, new HashSet<int>());
        var plan = SheetLayout.MakePlan(new[] { t }, M);
        Assert.Equal(SheetLayout.MAX_W, plan.ColW[0][0]);
        var lines = plan.Lines[0][1][0];
        Assert.InRange(lines.Count, 2, SheetLayout.MAX_LINES);
        Assert.Equal(words, string.Join(" ", lines));
        Assert.Equal(lines.Count * SheetLayout.LINE + 2 * SheetLayout.PAD_Y, plan.RowH[0][1]);
        var capped = SheetLayout.Wrap(string.Join(" ", Enumerable.Repeat("panjang", 200)), 100f, false, M);
        Assert.Equal(SheetLayout.MAX_LINES, capped.Count);
        Assert.EndsWith("…", capped[^1]);
        Assert.All(capped, l => Assert.True(M(l, false) <= 100f));
    }

    [Fact]
    public void EmptyEdgesTrimmedEmptySheetPrintsAndEachSheetOwnPage()
    {
        var rows = new[] { new[] { "A", "B", "", "", "" }, new[] { "1", "2", "", "", "" } }.Concat(Enumerable.Repeat(new[] { "", "", "", "", "" }, 20));
        var p = Assert.Single(SheetLayout.MakePlan(new[] { G(rows) }, M).Pages);
        Assert.Equal(new[] { 0, 1 }, p.Cols); Assert.Equal(new[] { 0, 1 }, p.Rows);
        var empty = Assert.Single(SheetLayout.MakePlan(new[] { G(Enumerable.Repeat(new[] { "", "", "" }, 5)) }, M).Pages);
        Assert.Empty(empty.Rows); Assert.True(empty.Title);
        var plan = SheetLayout.MakePlan(new[] { G(new[] { new[] { "A" }, new[] { "1" } }), G(new[] { new[] { "B" }, new[] { "2" } }) }, M);
        Assert.Equal(new[] { 0, 1 }, plan.Pages.Select(x => x.Grid));
        Assert.All(plan.Pages, x => Assert.True(x.Title));
    }
}
