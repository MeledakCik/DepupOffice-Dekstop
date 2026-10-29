using Depup.Core.Interfaces;
using Depup.Core.Models;

namespace Depup.Core.Services;

public sealed class DocumentService : IDocumentService
{
    public IReadOnlyList<DocumentItem> GetRecentDocuments() =>
    [
        new("Surat Pengantar.pdf", "PDF", "2.4 MB", DateTime.Now.AddMinutes(-12)),
        new("Laporan Keuangan.xlsx", "XLSX", "840 KB", DateTime.Now.AddHours(-2)),
        new("Kontrak Kerja.docx", "DOCX", "1.1 MB", DateTime.Now.AddDays(-1)),
        new("Scan KTP.jpg", "JPG", "3.8 MB", DateTime.Now.AddDays(-2))
    ];
}
