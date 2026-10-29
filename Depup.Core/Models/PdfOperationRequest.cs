namespace Depup.Core.Models;

public sealed record PdfOperationRequest(string Operation, IReadOnlyList<string> Inputs, string OutputPath, string? Options = null);
