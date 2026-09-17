using System.Xml.Linq;
using OGKToolBox.Core.Abstractions;
using OGKToolBox.Core.Models;

namespace OGKToolBox.Infrastructure.Scanning;

public sealed class DataPackageResolver : IDataPackageResolver
{
    public Task<IReadOnlyList<DataPackage>> DiscoverAsync(GameInstallation installation, CancellationToken cancellationToken)
    {
        return Task.Run<IReadOnlyList<DataPackage>>(() =>
        {
            var baseVersion = ReadVersion(installation.BaseGameDataPath);
            var packages = new List<DataPackage> { new("A000", installation.BaseGameDataPath, baseVersion, 0, true) };

            var optionDirectories = installation.UpdatePackagePaths
                .Where(Directory.Exists)
                .SelectMany(root => Directory.EnumerateDirectories(root, "*", SearchOption.TopDirectoryOnly))
                .Where(path => IsPackageId(Path.GetFileName(path)) &&
                    !Path.GetFileName(path).Equals("A000", StringComparison.OrdinalIgnoreCase))
                .OrderBy(path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var loadedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var path in optionDirectories)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var version = ReadVersion(path);
                if (version.Major != baseVersion.Major || version.Minor != baseVersion.Minor)
                {
                    continue;
                }
                // Keep the existing option copy authoritative when both roots contain an ID.
                if (!loadedIds.Add(Path.GetFileName(path))) continue;
                packages.Add(new(Path.GetFileName(path), path, version, packages.Count, false));
            }

            return packages;
        }, cancellationToken);
    }

    private static bool IsPackageId(string id) =>
        id.Length == 4 && char.ToUpperInvariant(id[0]) == 'A' && id.AsSpan(1).ToArray().All(char.IsLetterOrDigit);

    private static Version ReadVersion(string packagePath)
    {
        foreach (var file in new[] { "DataConfig.xml", "dataConfig.xml" })
        {
            var path = Path.Combine(packagePath, file);
            if (!File.Exists(path))
            {
                continue;
            }

            try
            {
                var document = XDocument.Load(path);
                var versionElement = document.Descendants().FirstOrDefault(element =>
                    element.Name.LocalName.Equals("version", StringComparison.OrdinalIgnoreCase));
                if (versionElement is not null
                    && int.TryParse(versionElement.Elements().FirstOrDefault(item => item.Name.LocalName == "major")?.Value, out var major)
                    && int.TryParse(versionElement.Elements().FirstOrDefault(item => item.Name.LocalName == "minor")?.Value, out var minor))
                {
                    _ = int.TryParse(versionElement.Elements().FirstOrDefault(item => item.Name.LocalName == "release")?.Value, out var release);
                    return new Version(major, minor, release);
                }
            }
            catch
            {
                // A broken version file must not stop discovery; the scanner reports bad content later.
            }
        }

        return new Version(0, 0);
    }
}
