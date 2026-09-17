using System.Collections.ObjectModel;

namespace OGKToolBox.Core.Models;

public enum ResourceKind
{
    Unknown,
    Jacket,
    Card,
    CardCharacter,
    CardIcon,
    Character,
    ChapterCharacter,
    UserPlate,
    Audio,
    Movie
}

public enum DiagnosticSeverity
{
    Information,
    Warning,
    Error
}

public sealed record GameInstallation(string RootPath)
{
    public string StreamingAssetsPath => Path.Combine(RootPath, "mu3_Data", "StreamingAssets");
    public string BaseGameDataPath => Path.Combine(StreamingAssetsPath, "GameData", "A000");
    public string BaseAssetsPath => Path.Combine(StreamingAssetsPath, "assets");
    public string AmfsPath => Path.Combine(RootPath, "amfs");
    public string OptionPath => Path.Combine(RootPath, "option");
    public string GameDataPath => Path.Combine(StreamingAssetsPath, "GameData");
    public IReadOnlyList<string> UpdatePackagePaths => [OptionPath, GameDataPath];
    public string ToolDataPath => Path.Combine(RootPath, "Tools", "OGKToolBox");
    public string IndexPath => Path.Combine(ToolDataPath, "index", "library.db");
    public string AudioCachePath => Path.Combine(ToolDataPath, "cache", "audio");
    public string ConfigurationBackupPath => Path.Combine(ToolDataPath, "backups", "configuration");
    public string LogPath => Path.Combine(ToolDataPath, "logs");
    public string TemporaryPath => Path.Combine(ToolDataPath, "temp");
}

public sealed record LibraryQuery(
    int Offset = 0,
    int Limit = 50,
    string Search = "",
    string Sort = "id",
    bool Descending = false,
    IReadOnlyDictionary<string, string[]>? Filters = null)
{
    public int SafeOffset => Math.Max(0, Offset);
    // Desktop library views use virtual rendering, so a larger page is safe and avoids hundreds of local IPC requests.
    public int SafeLimit => Math.Clamp(Limit, 1, 5000);
    public IReadOnlyDictionary<string, string[]> EffectiveFilters => Filters ??
        new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
}

public sealed record IndexedItem<T>(string Id, T Value);
public sealed record LibraryPage<T>(IReadOnlyList<IndexedItem<T>> Items, int Total, int Offset, int Limit);

public sealed record DataPackage(
    string Id,
    string RootPath,
    Version Version,
    int LoadOrder,
    bool IsBaseGame);

public sealed record GameVersionInfo(Version AppImageVersion, int OptionRelease, bool IsAmDaemonDetected)
{
    public string Display => GameVersionFormatter.FormatDataVersion(AppImageVersion, OptionRelease);

    public static GameVersionInfo FromDataPackages(IReadOnlyList<DataPackage> packages)
    {
        var basePackage = packages.FirstOrDefault(package => package.IsBaseGame);
        var dataVersion = basePackage?.Version ?? new Version(0, 0);
        var optionRelease = packages.Where(package => !package.IsBaseGame)
            .Select(package => package.Version.Build)
            .DefaultIfEmpty(0)
            .Max();
        return new(dataVersion, optionRelease, false);
    }
}

public static class GameVersionFormatter
{
    public static string Format(Version appImageVersion, int optionRelease)
    {
        ArgumentNullException.ThrowIfNull(appImageVersion);
        var version = $"{appImageVersion.Major}.{appImageVersion.Minor:D2}";
        var suffix = ReleaseToAlphabet(optionRelease);
        return suffix is null ? version : $"{version}-{suffix}";
    }

    public static string FormatDataVersion(Version dataVersion, int optionRelease)
    {
        ArgumentNullException.ThrowIfNull(dataVersion);
        var minor = dataVersion.Minor >= 10 ? dataVersion.Minor / 10 : dataVersion.Minor;
        var version = $"{dataVersion.Major}.{minor}";
        var suffix = ReleaseToAlphabet(optionRelease);
        return suffix is null ? version : $"{version}-{suffix}";
    }

    public static string? ReleaseToAlphabet(int release)
    {
        if (release <= 0) return null;

        var result = string.Empty;
        for (var value = release; value > 0; value = (value - 1) / 26)
        {
            result = (char)('A' + (value - 1) % 26) + result;
        }
        return result;
    }
}

public sealed record ResourceOrigin(
    string PackageId,
    string SourcePath,
    int LoadOrder,
    bool IsEffective = false);

public sealed record Chart(
    int Difficulty,
    string DifficultyName,
    decimal LevelConstant,
    string FilePath,
    string Creator,
    decimal MainBpm,
    int TotalNotes,
    int TapCount,
    int HoldCount,
    int FlickCount,
    int BellCount,
    bool Exists);

public sealed record AudioReference(string AcbPath, string AwbPath, int MusicSourceId);

public sealed record ResourceReference(string Key, string BundlePath, ResourceKind Kind);

public static class CharacterExpressionRules
{
    public static bool IsFaceLayer(string spriteName)
    {
        const string marker = "_Face_";
        if (!spriteName.StartsWith("Chara_", StringComparison.OrdinalIgnoreCase)) return false;
        var markerIndex = spriteName.LastIndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (markerIndex < 0) return false;
        var pattern = spriteName.AsSpan(markerIndex + marker.Length);
        return pattern.Length == 4
            && char.IsAsciiLetter(pattern[0])
            && pattern[1] == '_'
            && char.IsAsciiDigit(pattern[2])
            && char.IsAsciiDigit(pattern[3]);
    }

    public static bool TryGetModelIdFromBundle(string resourceKey, out int modelId)
    {
        const string prefix = "anm_chara_";
        modelId = 0;
        if (!resourceKey.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;
        var identifier = resourceKey.AsSpan(prefix.Length);
        if (identifier.Length < 8) return false;
        for (var index = 0; index < 8; index++)
            if (!char.IsAsciiDigit(identifier[index])) return false;
        for (var index = 0; index < 6; index++)
            modelId = modelId * 10 + identifier[index] - '0';
        return modelId > 0;
    }
}

public sealed record Music(
    int Id,
    string DataName,
    string Title,
    string Artist,
    string Genre,
    string VersionName,
    DateTime? ReleaseDate,
    IReadOnlyList<Chart> Charts,
    ResourceReference? Jacket,
    AudioReference? Audio,
    ResourceOrigin Origin);

public sealed record Card(
    int Id,
    string DataName,
    string Name,
    int CharacterId,
    string CharacterName,
    string NickName,
    string Rarity,
    string Attribute,
    ResourceReference? Image,
    ResourceReference? CharacterImage,
    ResourceReference? FullIllustration,
    ResourceReference? Icon,
    ResourceOrigin Origin);

public sealed record CardFilterCriteria(
    IReadOnlySet<int> CharacterIds,
    IReadOnlySet<string> Rarities,
    IReadOnlySet<string> PackageIds)
{
    public bool Matches(Card card) =>
        (CharacterIds.Count == 0 || CharacterIds.Contains(card.CharacterId))
        && (Rarities.Count == 0 || Rarities.Contains(card.Rarity))
        && (PackageIds.Count == 0 || PackageIds.Contains(card.Origin.PackageId));
}

public sealed record Character(
    int Id,
    string DataName,
    string Name,
    int ModelId,
    int GraphicCardId,
    string FlavorText,
    IReadOnlyList<ResourceReference> Images,
    ResourceOrigin Origin);

public sealed record GameResource(
    string Key,
    ResourceKind Kind,
    string BundlePath,
    long Size,
    ResourceOrigin Origin);

public sealed record LibraryDiagnostic(
    DiagnosticSeverity Severity,
    string Code,
    string Message,
    string? SourcePath = null);

public sealed class LibrarySnapshot
{
    public required GameInstallation Installation { get; init; }
    public GameVersionInfo? GameVersion { get; set; }
    public required IReadOnlyList<DataPackage> Packages { get; init; }
    public required IReadOnlyList<Music> MusicVariants { get; init; }
    public required IReadOnlyList<Music> EffectiveMusic { get; init; }
    public required IReadOnlyList<Card> Cards { get; init; }
    public required IReadOnlyList<Character> Characters { get; init; }
    public required IReadOnlyList<GameResource> Resources { get; init; }
    public required IReadOnlyList<LibraryDiagnostic> Diagnostics { get; init; }

    public static LibrarySnapshot Empty(GameInstallation installation) => new()
    {
        Installation = installation,
        GameVersion = null,
        Packages = [],
        MusicVariants = [],
        EffectiveMusic = [],
        Cards = [],
        Characters = [],
        Resources = [],
        Diagnostics = []
    };
}

public sealed record ScanProgress(
    string Phase,
    int Completed,
    int Total,
    string? CurrentPath = null,
    int OverallCompleted = 0,
    int OverallTotal = 0)
{
    public double Percent => Total <= 0 ? 0 : Math.Clamp((double)Completed / Total * 100, 0, 100);
    public double OverallPercent => OverallTotal <= 0
        ? Percent
        : Math.Clamp((double)OverallCompleted / OverallTotal * 100, 0, 100);
}
