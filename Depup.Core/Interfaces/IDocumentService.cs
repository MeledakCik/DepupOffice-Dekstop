using Depup.Core.Models;

namespace Depup.Core.Interfaces;

public interface IDocumentService
{
    IReadOnlyList<DocumentItem> GetRecentDocuments();
}
