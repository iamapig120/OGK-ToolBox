using OGKToolBox.Core.Models;

namespace OGKToolBox.Application.Contracts;

public sealed record InstallationSession(string Id, string DisplayName, GameInstallation Installation);

public enum ScanJobState { Pending, Running, Completed, Failed, Cancelled }

public sealed record ScanJobStatus(
    string Id,
    string InstallationId,
    ScanJobState State,
    string Phase,
    int Completed,
    int Total,
    string? CurrentItem,
    string? Error,
    DateTimeOffset CreatedAt,
    DateTimeOffset? FinishedAt,
    int OverallCompleted = 0,
    int OverallTotal = 0)
{
    public double Percent => Total <= 0 ? 0 : Math.Clamp((double)Completed / Total * 100, 0, 100);
    public double OverallPercent => OverallTotal <= 0
        ? Percent
        : Math.Clamp((double)OverallCompleted / OverallTotal * 100, 0, 100);
}

public sealed record LibrarySummary(
    string InstallationId,
    int MusicCount,
    int CardCount,
    int CharacterCount,
    int ResourceCount,
    int DiagnosticCount,
    string GameVersion,
    DateTimeOffset? LastScanAt);

public sealed record FacetValue(string Value, int Count);
public sealed record LibraryFacets(
    IReadOnlyList<FacetValue> Genres,
    IReadOnlyList<FacetValue> Packages,
    IReadOnlyList<FacetValue> Rarities,
    IReadOnlyList<FacetValue> Attributes,
    IReadOnlyList<FacetValue> ResourceKinds,
    IReadOnlyList<FacetValue> DiagnosticSeverities);

public sealed record BackupInfo(string Id, GameConfigurationFileKind Kind, DateTimeOffset CreatedAt, long Size, string ContentHash);
public sealed record RestoreResult(GameConfigurationFileKind Kind, string ContentHash, DateTimeOffset RestoredAt);

public sealed record ConfigurationPreviewTicket(
    string Token,
    bool CanSave,
    IReadOnlyList<string> ValidationErrors,
    string ProposedHash,
    DateTimeOffset ExpiresAt);

public sealed record ConfigurationFileView(
    GameConfigurationFileKind Kind,
    string DisplayName,
    bool Exists,
    string EncodingName,
    string NewLineName,
    DateTimeOffset? LastWriteTime,
    long Size,
    IReadOnlyList<ConfigurationEntry> Entries,
    string ContentHash);

public sealed record InstalledModView(
    string Id,
    string Name,
    string FileName,
    InstalledModKind Kind,
    bool IsEnabled,
    string Version,
    DateTimeOffset LastWriteTime,
    long Size,
    string ChineseName,
    string Description,
    IReadOnlyList<ConfigurationEntry> ConfigurationEntries);

public sealed record ConfigurationView(
    string InstallationId,
    string HookVersion,
    IReadOnlyList<ConfigurationFileView> Files,
    IReadOnlyList<InstalledModView> Mods,
    IReadOnlyList<LibraryDiagnostic> Diagnostics);

public sealed record ConfigurationSaveInfo(
    GameConfigurationFileKind Kind,
    string BackupId,
    string ContentHash,
    DateTimeOffset SavedAt);

public sealed record ModToggleInfo(string ModId, bool IsEnabled, string ContentHash, DateTimeOffset ChangedAt);

public sealed record BinaryContent(string FileName, string ContentType, Stream Content);
public sealed record ExpressionInfo(string Id, string Name, string BundleKey);
public sealed record IoDllCatalog(string DirectoryName, IReadOnlyList<string> Dlls);

public sealed record OptionPackageView(
    string Name,
    string DirectoryPath,
    bool IsValid,
    bool IsLoaded,
    string StatusText,
    string Version,
    int FileCount,
    long Size,
    DateTimeOffset? LastWriteTime);

public sealed record OptionDirectoryView(
    string DirectoryPath,
    bool Exists,
    int TotalFiles,
    long TotalSize,
    DateTimeOffset? LastWriteTime,
    IReadOnlyList<OptionPackageView> Packages)
{
    public IReadOnlyList<string> DirectoryPaths { get; init; } = [];
}
