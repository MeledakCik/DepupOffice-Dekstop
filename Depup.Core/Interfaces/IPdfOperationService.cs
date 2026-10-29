using Depup.Core.Models;

namespace Depup.Core.Interfaces;

public interface IPdfOperationService
{
    Task ExecuteAsync(PdfOperationRequest request, IProgress<double>? progress = null, CancellationToken cancellationToken = default);
}
