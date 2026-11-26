using System.IO.Compression;
using System.Text;
using DepupOfficeDesktop.Core;
using Xunit;

namespace DepupOfficeDesktop.Tests;

/// <summary>Port LinkFetchTest.kt (Android 4.4.1).</summary>
public sealed class LinkFetchTests
{
    [Fact]
    public void ExtractsUrlAndTitleFromShareText()
    {
        var t = "DDL_DML.pdf https://share.google/3lwcufoNQdQ4Jvv01";
        var u = LinkFetch.ExtractUrl(t);
        Assert.Equal("https://share.google/3lwcufoNQdQ4Jvv01", u);
        Assert.Equal("DDL_DML.pdf", LinkFetch.TitleOf(t, u!));
        Assert.Equal("https://x.id/a.pdf", LinkFetch.ExtractUrl("Lihat (https://x.id/a.pdf)."));
        Assert.Null(LinkFetch.ExtractUrl("http://tidak-aman.id/a.pdf"));
        Assert.Null(LinkFetch.ExtractUrl("tanpa link"));
    }

    [Fact]
    public void RewritesGoogleDriveAndDocs()
    {
        const string drive = "https://drive.usercontent.google.com/download?id=1AbCdEfGhIjK&export=download&confirm=t";
        Assert.Equal(drive, LinkFetch.DirectUrl("https://drive.google.com/file/d/1AbCdEfGhIjK/view?usp=sharing"));
        Assert.Equal(drive, LinkFetch.DirectUrl("https://drive.google.com/open?id=1AbCdEfGhIjK"));
        Assert.Equal("https://docs.google.com/document/d/1AbCdEfGhIjK/export?format=pdf", LinkFetch.DirectUrl("https://docs.google.com/document/d/1AbCdEfGhIjK/edit"));
        Assert.Equal("https://docs.google.com/presentation/d/1AbCdEfGhIjK/export/pdf", LinkFetch.DirectUrl("https://docs.google.com/presentation/d/1AbCdEfGhIjK/edit"));
        Assert.Equal("https://lms.unikom.ac.id/a.pdf", LinkFetch.DirectUrl("https://lms.unikom.ac.id/a.pdf"));
    }

    [Fact]
    public void SniffsByContentNotHeader()
    {
        Assert.Equal(LinkFetch.Kind.Pdf, LinkFetch.Sniff(Encoding.ASCII.GetBytes("%PDF-1.7\n..."), "x"));
        Assert.Equal(LinkFetch.Kind.Pdf, LinkFetch.Sniff(Encoding.UTF8.GetBytes("﻿\n%PDF-1.4"), "x"));
        Assert.Null(LinkFetch.Sniff(Encoding.ASCII.GetBytes("<!DOCTYPE html><html>login</html>"), "a.pdf"));
        var ms = new MemoryStream();
        using (var z = new ZipArchive(ms, ZipArchiveMode.Create, true)) z.CreateEntry("word/document.xml");
        Assert.Equal(LinkFetch.Kind.Docx, LinkFetch.Sniff(ms.ToArray(), "x"));
    }

    [Fact]
    public void SafeFileNames()
    {
        Assert.Equal("Materi SQL.pdf", LinkFetch.FileName("attachment; filename=\"Materi SQL.pdf\"", "https://a.id/download", ""));
        Assert.Equal("Bab 1.pdf", LinkFetch.FileName("attachment; filename*=UTF-8''Bab%201.pdf", "https://a.id/x", ""));
        Assert.Equal("DDL_DML.pdf", LinkFetch.FileName(null, "https://drive.usercontent.google.com/download", "DDL_DML.pdf"));
        Assert.DoesNotContain("/", LinkFetch.FileName("attachment; filename=\"../etc/passwd\"", "https://a.id/x", ""));
    }
}
