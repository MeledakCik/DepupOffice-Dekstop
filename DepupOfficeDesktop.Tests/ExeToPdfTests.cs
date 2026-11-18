using System.Drawing;
using System.Security.Cryptography;
using DepupOfficeDesktop.Core;
using Xunit;

namespace DepupOfficeDesktop.Tests;

/// <summary>EXE → PDF (port ExeToPdf.kt): PDF di overlay program PE, polos / XOR VaySoft / XOR kunci tak dikenal.</summary>
public sealed class ExeToPdfTests
{
    private static byte[] SamplePdf()
    {
        var c = new PdfCanvas();
        for (int i = 0; i < 15; i++)
        {
            if (i > 0) c.NewPage();
            c.Text($"Halaman {i + 1} — uji EXE ke PDF", 60, 80, 18, Color.Black, true);
            c.FillRect(new RectangleF(60, 120, 200, 40), Color.SteelBlue);
        }
        return c.ToBytes();
    }

    /// <summary>Program PE minimal: header MZ/PE + 1 section, lalu overlay.</summary>
    private static byte[] WrapPe(byte[] overlay)
    {
        var pe = new byte[0x300];
        pe[0] = (byte)'M'; pe[1] = (byte)'Z';
        BitConverter.GetBytes(0x40).CopyTo(pe, 0x3C);
        pe[0x40] = (byte)'P'; pe[0x41] = (byte)'E';
        BitConverter.GetBytes((ushort)1).CopyTo(pe, 0x40 + 6);       // jumlah section
        BitConverter.GetBytes((ushort)0).CopyTo(pe, 0x40 + 20);      // ukuran optional header
        int sec = 0x40 + 24;
        BitConverter.GetBytes(0x100).CopyTo(pe, sec + 16);           // SizeOfRawData
        BitConverter.GetBytes(0x200).CopyTo(pe, sec + 20);           // PointerToRawData → akhir 0x300
        return pe.Concat(overlay).ToArray();
    }

    private static byte[] Xor(byte[] d, byte[] key, int phase = 0) => d.Select((b, i) => (byte)(b ^ key[(i + phase) % key.Length])).ToArray();

    [Fact]
    public void OverlayStartFollowsLastSection() => Assert.Equal(0x300, ExeToPdf.OverlayStart(WrapPe(new byte[10])));

    [Fact]
    public void PlainPdfInOverlay()
    {
        var pdf = SamplePdf();
        var r = ExeToPdf.Extract(WrapPe(pdf));
        Assert.Equal(pdf, r.Pdf);
        Assert.Null(r.KeyHex);
    }

    [Fact]
    public void VaySoftKey()
    {
        var pdf = SamplePdf();
        byte[] vs = { 0x00, 0x0B, 0x0C, 0x04, 0x0D, 0x03, 0x04, 0x0B, 0x0C, 0x03, 0x06, 0x02 };
        var r = ExeToPdf.Extract(WrapPe(Xor(pdf, vs)));
        Assert.Equal(pdf, r.Pdf);
        Assert.Equal("Kunci VaySoft", r.Method);
    }

    [Fact]
    public void UnknownKeyRecoveredFromXrefTable()
    {
        var pdf = SamplePdf();
        var key = RandomNumberGenerator.GetBytes(12);
        var r = ExeToPdf.Extract(WrapPe(Xor(pdf, key)));
        Assert.Equal(pdf, r.Pdf);
        Assert.StartsWith("Kunci dipulihkan", r.Method);
        Assert.Equal(15, PdfDepupOpen(r.Pdf));
    }

    [Fact]
    public void NotAPdfWrapperFailsClearly()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => ExeToPdf.Extract(WrapPe(RandomNumberGenerator.GetBytes(5000))));
        Assert.Contains("PDF tidak ditemukan", ex.Message);
    }

    private static int PdfDepupOpen(byte[] pdf) => PdfFile.Open(pdf).Pages().Count;
}
