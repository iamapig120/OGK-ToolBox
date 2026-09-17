using OGKToolBox.Core.Models;

namespace OGKToolBox.Core.Abstractions;

public interface IGameInstallationValidator
{
    bool TryValidate(string rootPath, out GameInstallation? installation, out string? error);
}

public interface IInstallationFileCatalog
{
    Task<IReadOnlyList<string>> ListRootDllsAsync(GameInstallation installation, CancellationToken cancellationToken);
}

public interface ILocalFileSystem
{
    void CreateDirectory(string path);
    bool FileExists(string path);
    DateTimeOffset? GetLastWriteTimeUtc(string path);
    Stream OpenRead(string path);
}

public interface IGameConfigurationInspector
{
    Task<GameConfigurationSnapshot> InspectAsync(
        GameInstallation installation,
        CancellationToken cancellationToken);
}

public interface IGameConfigurationEditor
{
    Task<ConfigurationChangePreview> PreviewAsync(
        GameInstallation installation,
        GameConfigurationFileKind kind,
        IReadOnlyList<ConfigurationEdit> edits,
        string expectedBaselineHash,
        CancellationToken cancellationToken);

    Task<ConfigurationSaveResult> SaveAsync(
        GameInstallation installation,
        ConfigurationChangePreview confirmedPreview,
        IReadOnlyList<ConfigurationEdit> edits,
        string backupRoot,
        CancellationToken cancellationToken);
}

public interface IModManager
{
    Task<ModTogglePreview> PreviewToggleAsync(
        GameInstallation installation,
        InstalledMod mod,
        CancellationToken cancellationToken);

    Task<ModToggleResult> ApplyToggleAsync(
        GameInstallation installation,
        ModTogglePreview confirmedPreview,
        string operationRoot,
        CancellationToken cancellationToken);
}

public interface IDataPackageResolver
{
    Task<IReadOnlyList<DataPackage>> DiscoverAsync(GameInstallation installation, CancellationToken cancellationToken);
}

public interface IGameVersionDetector
{
    Task<GameVersionInfo?> DetectAsync(
        GameInstallation installation,
        IReadOnlyList<DataPackage> packages,
        CancellationToken cancellationToken);

    Task ReleaseAsync(GameInstallation installation, CancellationToken cancellationToken);
}

public interface IMusicMetadataReader
{
    Music Read(string musicXmlPath, DataPackage package, IReadOnlyDictionary<string, GameResource> effectiveResources);
}

public interface IChartSerializer
{
    OgkrDocument Read(string path);
    string WriteToString(OgkrDocument document);
}

public interface IChartPreviewBuilder
{
    ChartPreview Build(string path);
}

public interface ILibraryScanner
{
    Task<LibrarySnapshot?> LoadCachedAsync(
        GameInstallation installation,
        CancellationToken cancellationToken);

    Task<LibrarySnapshot> ScanOptionAsync(
        GameInstallation installation,
        LibrarySnapshot cached,
        IProgress<ScanProgress>? progress,
        CancellationToken cancellationToken);

    Task<LibrarySnapshot> ScanAsync(
        GameInstallation installation,
        IProgress<ScanProgress>? progress,
        CancellationToken cancellationToken);
}

public interface IUnityResourceReader
{
    bool IsUnityFsBundle(string path);
    Task<IReadOnlyList<UnityImageInfo>> ListImagesAsync(string bundlePath, CancellationToken cancellationToken);
    Task<IReadOnlyList<UnitySpriteInfo>> ListSpritesAsync(string bundlePath, CancellationToken cancellationToken);
    Task<UnityImage?> ReadImageAsync(string bundlePath, long pathId, CancellationToken cancellationToken);
    Task<UnityImage?> ReadSpriteAsync(string bundlePath, long pathId, CancellationToken cancellationToken);
    Task<UnityImage?> ReadCharacterExpressionAsync(string bundlePath, long faceSpritePathId, CancellationToken cancellationToken);
    Task<UnityImage?> ReadFirstImageAsync(string bundlePath, CancellationToken cancellationToken);
    Task<UnityImage?> ReadFirstThumbnailAsync(string bundlePath, int maxSize, CancellationToken cancellationToken);
    Task<UnityAssetGraph> ReadAssetGraphAsync(string bundlePath, CancellationToken cancellationToken);
    Task<IReadOnlyDictionary<string, UnityImage>> ReadNamedImagesFromAssetsFileAsync(
        string assetsPath, IReadOnlyCollection<string> names, CancellationToken cancellationToken);
}

public sealed record UnityImageInfo(long PathId, string Name, int Width, int Height);
public sealed record UnitySpriteInfo(long PathId, string Name, int Width, int Height);
public sealed record UnityImage(string Name, int Width, int Height, byte[] PngBytes);
public sealed record ThumbnailImage(byte[] Bytes, string ContentType = "image/png");

public sealed record UnityAssetGraph(
    string SourceFile,
    string UnityVersion,
    IReadOnlyList<UnityAssetExternal> Externals,
    IReadOnlyList<UnityAssetNode> Assets,
    IReadOnlyList<UnityAssetReference> References);
public sealed record UnityAssetExternal(int FileId, string PathName);
public sealed record UnityAssetNode(long PathId, int ClassId, string ClassName, string? Name);
public sealed record UnityAssetReference(
    long SourcePathId,
    string FieldPath,
    int TargetFileId,
    long TargetPathId);

public interface IChartRenderAssetManifestBuilder
{
    Task<ChartRenderAssetManifest> BuildAsync(string gameRoot, CancellationToken cancellationToken);
}

public sealed record ChartRenderAssetManifest(
    string Schema,
    string Target,
    string UnityVersion,
    IReadOnlyList<ChartRenderAssetSource> Sources,
    IReadOnlyList<ChartRenderAssetAssignment> Assignments,
    IReadOnlyList<ChartRenderAssetNode> Assets,
    IReadOnlyList<ChartRenderAssetEdge> References);
public sealed record ChartRenderAssetSource(string File, string Sha256);
public sealed record ChartRenderAssetAssignment(
    string Group,
    string Name,
    string SourceFile,
    long SourcePathId,
    string TargetFile,
    long TargetPathId);
public sealed record ChartRenderAssetNode(
    string File,
    long PathId,
    int ClassId,
    string ClassName,
    string? Name);
public sealed record ChartRenderAssetEdge(
    string SourceFile,
    long SourcePathId,
    string FieldPath,
    string TargetFile,
    long TargetPathId);

public interface IChartRenderDescriptorBuilder
{
    ChartRenderSceneDescriptor Build(ChartRenderAssetManifest manifest);
}

public sealed record ChartRenderSceneDescriptor(
    string Schema,
    string Target,
    string UnityVersion,
    IReadOnlyList<ChartRenderAssetSource> Sources,
    IReadOnlyList<ChartRenderEntryDescriptor> Entries);
public sealed record ChartRenderEntryDescriptor(
    string Group,
    string Name,
    ChartRenderAssetHandle Root,
    IReadOnlyList<ChartRenderAssetHandle> Objects,
    IReadOnlyList<ChartRenderAssetHandle> Meshes,
    IReadOnlyList<ChartRenderAssetHandle> Materials,
    IReadOnlyList<ChartRenderAssetHandle> Textures,
    IReadOnlyList<ChartRenderAssetHandle> Shaders,
    IReadOnlyList<ChartRenderUnresolvedReference> UnresolvedReferences);
public sealed record ChartRenderAssetHandle(
    string File,
    long PathId,
    int ClassId,
    string ClassName,
    string? Name);
public sealed record ChartRenderUnresolvedReference(
    string SourceFile,
    long SourcePathId,
    string FieldPath,
    string TargetFile,
    long TargetPathId);

public interface IAudioPreviewService
{
    bool IsAvailable { get; }
    Task<string> DecodeToWaveAsync(AudioReference audio, string cacheDirectory, CancellationToken cancellationToken);
}

public interface IResourceExporter
{
    Task ExportFileAsync(string sourcePath, string destinationPath, CancellationToken cancellationToken);
    Task ExportImageAsync(ResourceReference resource, string destinationPath, CancellationToken cancellationToken);
    Task ExportIndexJsonAsync(LibrarySnapshot snapshot, string destinationPath, CancellationToken cancellationToken);
    Task ExportMusicCsvAsync(IReadOnlyList<Music> music, string destinationPath, CancellationToken cancellationToken);
    Task ExportUnityAssetGraphJsonAsync(string bundlePath, string destinationPath, CancellationToken cancellationToken);
    Task ExportChartRenderAssetManifestJsonAsync(string gameRoot, string destinationPath, CancellationToken cancellationToken);
}

public interface ILibraryIndex
{
    Task InitializeAsync(CancellationToken cancellationToken);
    Task<LibrarySnapshot?> LoadAsync(GameInstallation installation, CancellationToken cancellationToken);
    Task<(int MusicCount, int CardCount, int CharacterCount, int ResourceCount, int DiagnosticCount)?> GetCountsAsync(GameInstallation installation, CancellationToken cancellationToken);
    Task<bool> IsSourceCurrentAsync(GameInstallation installation, CancellationToken cancellationToken);
    Task<bool> IsAmfsCurrentAsync(GameInstallation installation, CancellationToken cancellationToken);
    Task SaveAsync(LibrarySnapshot snapshot, CancellationToken cancellationToken);
    Task<LibraryPage<Music>> QueryMusicAsync(GameInstallation installation, LibraryQuery query, CancellationToken cancellationToken);
    Task<LibraryPage<Card>> QueryCardsAsync(GameInstallation installation, LibraryQuery query, CancellationToken cancellationToken);
    Task<LibraryPage<Character>> QueryCharactersAsync(GameInstallation installation, LibraryQuery query, CancellationToken cancellationToken);
    Task<LibraryPage<GameResource>> QueryResourcesAsync(GameInstallation installation, LibraryQuery query, CancellationToken cancellationToken);
    Task<LibraryPage<LibraryDiagnostic>> QueryDiagnosticsAsync(GameInstallation installation, LibraryQuery query, CancellationToken cancellationToken);
}
