namespace Depup.Core.Models;

public sealed record DocumentItem(string Name, string Type, string Size, DateTime Modified, bool IsFavorite = false);
