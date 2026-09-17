using System.Security.Cryptography;
using OGKToolBox.Application.Services;

namespace OGKToolBox.Tests;

public sealed class ApplicationSecurityTests
{
    [Fact]
    public void OpaqueIdsAreScopedTypedAndRejectTampering()
    {
        var service = new OpaqueIdService(RandomNumberGenerator.GetBytes(32));
        var id = service.Create("resource", "installation-a", "logical-resource");

        Assert.Equal(["installation-a", "logical-resource"], service.Read(id, "resource"));
        Assert.Throws<KeyNotFoundException>(() => service.Read(id, "music"));

        var replacement = id[^1] == 'A' ? 'B' : 'A';
        Assert.Throws<KeyNotFoundException>(() => service.Read(id[..^1] + replacement, "resource"));
    }
}
