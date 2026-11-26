using DepupOfficeDesktop.Core;
using Xunit;

namespace DepupOfficeDesktop.Tests;

/// <summary>Port SummarizerTest.kt (Android 4.4): baris PDF disambung, judul terpisah, isi berulang dihitung sekali.</summary>
public sealed class SummarizerTests
{
    [Fact]
    public void JoinsWrappedPdfLinesAndDropsDuplicates()
    {
        var text = "Kegiatan bulan ini berjalan lancar. Kesimpulan penting: anggaran disetujui\n" +
            "dan laporan dikirim tepat waktu.\n\nKegiatan bulan ini berjalan lancar. Kesimpulan penting: anggaran disetujui\n" +
            "dan laporan dikirim tepat waktu.";
        var points = DocTools.KeyPoints(text);
        Assert.Single(points);
        Assert.Equal("Kegiatan bulan ini berjalan lancar. Kesimpulan penting: anggaran disetujui dan laporan dikirim tepat waktu.", points[0]);
    }

    [Fact]
    public void HeadingStaysSeparateAndRepeatedPageCountsOnce()
    {
        var page = "Kegiatan bulan ini berjalan lancar. Kesimpulan penting: anggaran disetujui\n" +
            "dan laporan dikirim tepat waktu. Catatan: rapat berikutnya hari Senin pukul 09.00.";
        var points = DocTools.KeyPoints("Laporan Kegiatan\n" + page + "\nLampiran\n" + page);
        Assert.Single(points);
        Assert.StartsWith("Kegiatan bulan ini", points[0]);
    }
}
