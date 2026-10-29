using Xunit;
using Depup.Core.Services;

namespace Depup.Tests;

public sealed class DocumentServiceTests
{
    [Fact]
    public void ReturnsRecentDocuments()
    {
        var documents = new DocumentService().GetRecentDocuments();
        Assert.NotEmpty(documents);
        Assert.Contains(documents, document => document.Type == "PDF");
    }
}
