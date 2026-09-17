using OGKToolBox.Application.Contracts;
using OGKToolBox.Core.Abstractions;
using OGKToolBox.Core.Models;

namespace OGKToolBox.Application.Abstractions;

public interface IInstallationRegistry
{
    InstallationSession Register(string rootPath);
    InstallationSession GetRequired(string installationId);
    bool TryGet(string installationId, out InstallationSession? session);
}

public interface IOpaqueIdService
{
    string Create(string category, params string[] values);
    IReadOnlyList<string> Read(string id, string expectedCategory);
}

public interface ILibraryApplicationService
{
    Task<ScanJobStatus> StartScanAsync(string installationId, CancellationToken cancellationToken);
    ScanJobStatus GetScan(string jobId);
    bool CancelScan(string jobId);
    Task<LibrarySummary> GetSummaryAsync(string installationId, CancellationToken cancellationToken);
    Task<LibraryPage<Music>> QueryMusicAsync(string installationId, LibraryQuery query, CancellationToken cancellationToken);
    Task<LibraryPage<Card>> QueryCardsAsync(string installationId, LibraryQuery query, CancellationToken cancellationToken);
    Task<LibraryPage<Character>> QueryCharactersAsync(string installationId, LibraryQuery query, CancellationToken cancellationToken);
    Task<LibraryPage<GameResource>> QueryResourcesAsync(string installationId, LibraryQuery query, CancellationToken cancellationToken);
    Task<LibraryPage<LibraryDiagnostic>> QueryDiagnosticsAsync(string installationId, LibraryQuery query, CancellationToken cancellationToken);
    Task<LibraryFacets> GetFacetsAsync(string installationId, CancellationToken cancellationToken);
    Task<Music> GetMusicAsync(string installationId, string musicId, CancellationToken cancellationToken);
    Task<Card> GetCardAsync(string installationId, string cardId, CancellationToken cancellationToken);
    Task<Character> GetCharacterAsync(string installationId, string characterId, CancellationToken cancellationToken);
    Task<GameResource> GetResourceAsync(string installationId, string resourceId, CancellationToken cancellationToken);
}

public interface IConfigurationApplicationService
{
    Task<ConfigurationView> InspectAsync(string installationId, CancellationToken cancellationToken);
    Task<IoDllCatalog> ListIoDllsAsync(string installationId, CancellationToken cancellationToken);
    Task<ConfigurationPreviewTicket> PreviewAsync(string installationId, GameConfigurationFileKind kind,
        string baselineHash, IReadOnlyList<ConfigurationEdit> edits, CancellationToken cancellationToken);
    Task<ConfigurationSaveInfo> SaveAsync(string installationId, string previewToken, CancellationToken cancellationToken);
    Task<IReadOnlyList<BackupInfo>> ListBackupsAsync(string installationId, GameConfigurationFileKind kind, CancellationToken cancellationToken);
    Task<RestoreResult> RestoreAsync(string installationId, string backupId, CancellationToken cancellationToken);
    Task<ModToggleInfo> ToggleModAsync(string installationId, string modId, bool enable, CancellationToken cancellationToken);
}

public interface IResourceApplicationService
{
    Task<ThumbnailImage?> ReadThumbnailAsync(string installationId, string resourceId, int maxSize,
        CancellationToken cancellationToken);
    Task<ThumbnailImage?> ReadMusicJacketAsync(string installationId, string musicId, int maxSize,
        CancellationToken cancellationToken);
    Task<ThumbnailImage?> ReadCardImageAsync(string installationId, string cardId, string visual, int maxSize,
        CancellationToken cancellationToken);
    Task<ChartPreview> BuildChartPreviewAsync(string installationId, string chartId, CancellationToken cancellationToken);
    Task<string> DecodeMusicAsync(string installationId, string musicId, CancellationToken cancellationToken);
    Task<IReadOnlyList<UnityImage>> ReadChartEffectTexturesAsync(string installationId, CancellationToken cancellationToken);
    Task<IReadOnlyList<ExpressionInfo>> ListExpressionsAsync(string installationId, int modelId, CancellationToken cancellationToken);
    Task<UnityImage?> ReadExpressionAsync(string installationId, string expressionId, CancellationToken cancellationToken);
    Task<BinaryContent> ExportAsync(string installationId, string kind, string itemId, CancellationToken cancellationToken);
}

public interface IOptionPackageApplicationService
{
    Task<OptionDirectoryView> InspectAsync(string installationId, CancellationToken cancellationToken);
}

public interface IConfigurationBackupStore
{
    Task<IReadOnlyList<BackupInfo>> ListAsync(string installationId, GameInstallation installation, GameConfigurationFileKind kind,
        IOpaqueIdService ids, CancellationToken cancellationToken);
    Task<RestoreResult> RestoreAsync(GameInstallation installation, string backupFileName,
        GameConfigurationFileKind kind, CancellationToken cancellationToken);
    Task TrimAsync(GameInstallation installation, GameConfigurationFileKind kind, int retain,
        CancellationToken cancellationToken);
}
