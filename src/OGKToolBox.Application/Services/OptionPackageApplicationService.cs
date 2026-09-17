using System.Xml.Linq;
using OGKToolBox.Application.Abstractions;
using OGKToolBox.Application.Contracts;
using OGKToolBox.Core.Abstractions;
using OGKToolBox.Core.Models;

namespace OGKToolBox.Application.Services;

public sealed class OptionPackageApplicationService(
    IInstallationRegistry installations,
    IDataPackageResolver packageResolver) : IOptionPackageApplicationService
{
    public async Task<OptionDirectoryView> InspectAsync(string installationId, CancellationToken cancellationToken)
    {
        var installation = installations.GetRequired(installationId).Installation;
        var optionPath = Path.GetFullPath(installation.OptionPath);
        var roots = installation.UpdatePackagePaths.Where(Directory.Exists).ToArray();
        if (roots.Length == 0)
            return new(optionPath, false, 0, 0, null, []);

        var recognized = (await packageResolver.DiscoverAsync(installation, cancellationToken))
            .Where(package => !package.IsBaseGame)
            .ToDictionary(package => Path.GetFullPath(package.RootPath), StringComparer.OrdinalIgnoreCase);
        var directories = roots.SelectMany(root => Directory.EnumerateDirectories(root, "*", SearchOption.TopDirectoryOnly)
                .Where(path => !Path.GetFileName(path).Equals("A000", StringComparison.OrdinalIgnoreCase))
                .Where(path => root.Equals(optionPath, StringComparison.OrdinalIgnoreCase) || IsPackageId(Path.GetFileName(path))))
            .OrderBy(path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var packages = new List<OptionPackageView>(directories.Length);
        foreach (var directory in directories)
        {
            cancellationToken.ThrowIfCancellationRequested();
            packages.Add(ReadPackage(directory, recognized));
        }

        return new(optionPath, true, packages.Sum(package => package.FileCount), packages.Sum(package => package.Size),
            packages.Select(package => package.LastWriteTime).Max(), packages) { DirectoryPaths = roots };
    }

    private static OptionPackageView ReadPackage(
        string directory,
        IReadOnlyDictionary<string, DataPackage> recognized)
    {
        var name = Path.GetFileName(directory);
        var validName = IsPackageId(name);
        var normalized = Path.GetFullPath(directory);
        var isLoaded = recognized.ContainsKey(normalized);
        var version = ReadVersion(directory);
        var stats = ReadStats(directory);
        var versionText = version is null ? "未识别" : FormatVersion(version);
        var status = isLoaded ? "已加载" : validName ? "未加载" : "目录名无效";
        return new(name, normalized, validName && version is not null, isLoaded, status,
            versionText, stats.FileCount, stats.Size, GetLastWriteTime(directory));
    }

    private static bool IsPackageId(string id) =>
        id.Length == 4 && char.ToUpperInvariant(id[0]) == 'A' && id[1..].All(char.IsLetterOrDigit);

    private static Version? ReadVersion(string packagePath)
    {
        foreach (var file in new[] { "DataConfig.xml", "dataConfig.xml" })
        {
            var path = Path.Combine(packagePath, file);
            if (!File.Exists(path)) continue;
            try
            {
                var document = XDocument.Load(path);
                var element = document.Descendants().FirstOrDefault(item =>
                    item.Name.LocalName.Equals("version", StringComparison.OrdinalIgnoreCase));
                if (element is null || !int.TryParse(Value(element, "major"), out var major)
                    || !int.TryParse(Value(element, "minor"), out var minor)) continue;
                _ = int.TryParse(Value(element, "release"), out var release);
                return new Version(major, minor, release);
            }
            catch
            {
                return null;
            }
        }
        return null;
    }

    private static string? Value(XElement parent, string name) =>
        parent.Elements().FirstOrDefault(item => item.Name.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase))?.Value;

    private static string FormatVersion(Version version) =>
        version.Build >= 0 ? $"{version.Major}.{version.Minor}.{version.Build}" : $"{version.Major}.{version.Minor}";

    private static (int FileCount, long Size) ReadStats(string directory)
    {
        var count = 0;
        long size = 0;
        try
        {
            foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
            {
                count++;
                try { size += new FileInfo(file).Length; } catch (IOException) { }
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return (count, size);
    }

    private static DateTimeOffset? GetLastWriteTime(string path)
    {
        try { return new DateTimeOffset(Directory.GetLastWriteTimeUtc(path), TimeSpan.Zero); }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }
}
