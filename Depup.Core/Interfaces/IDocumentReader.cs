using Depup.Core.Models;

namespace Depup.Core.Interfaces;

public interface IDocumentReader
{
    Task<DocumentContent> ReadAsync(string path, CancellationToken cancellationToken = default);
}
