using OGKToolBox.Core.Abstractions;
using OGKToolBox.Core.Models;

namespace OGKToolBox.Infrastructure.Configuration;

public sealed class InstallationFileCatalog : IInstallationFileCatalog
{
    public Task<IReadOnlyList<string>> ListRootDllsAsync(GameInstallation installation,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!Directory.Exists(installation.RootPath)) return Task.FromResult<IReadOnlyList<string>>([]);
        var result = Directory.EnumerateFiles(installation.RootPath, "*.dll", SearchOption.TopDirectoryOnly)
            .Select(Path.GetFileName).OfType<string>()
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToArray();
        return Task.FromResult<IReadOnlyList<string>>(result);
    }
}
