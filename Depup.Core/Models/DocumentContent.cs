namespace Depup.Core.Models;

public sealed record DocumentContent(string Title, string Kind, IReadOnlyList<string> Sheets, IReadOnlyList<IReadOnlyList<IReadOnlyList<string>>> SheetRows);
