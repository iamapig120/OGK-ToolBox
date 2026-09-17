using Microsoft.Extensions.DependencyInjection;
using OGKToolBox.Core.Abstractions;
using OGKToolBox.Infrastructure.Audio;
using OGKToolBox.Infrastructure.Charts;
using OGKToolBox.Infrastructure.Indexing;
using OGKToolBox.Infrastructure.Resources;
using OGKToolBox.Infrastructure.Scanning;
using OGKToolBox.Infrastructure.Configuration;
using OGKToolBox.Infrastructure.Files;
using OGKToolBox.Application.Abstractions;

namespace OGKToolBox.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddOGKToolBoxInfrastructure(this IServiceCollection services, string toolsPath)
    {
        services.AddSingleton<IGameInstallationValidator, GameInstallationValidator>();
        services.AddSingleton<ILocalFileSystem, LocalFileSystem>();
        services.AddSingleton<IInstallationFileCatalog, InstallationFileCatalog>();
        services.AddSingleton<IGameConfigurationInspector, GameConfigurationInspector>();
        services.AddSingleton<IGameConfigurationEditor, GameConfigurationEditor>();
        services.AddSingleton<IConfigurationBackupStore, ConfigurationBackupStore>();
        services.AddSingleton<IModManager, ModManager>();
        services.AddSingleton<IDataPackageResolver, DataPackageResolver>();
        services.AddSingleton<IChartSerializer, OgkrSerializer>();
        services.AddSingleton<IChartPreviewBuilder, OgkrChartPreviewBuilder>();
        services.AddSingleton<IMusicMetadataReader, MusicMetadataReader>();
        var classDataPath = Path.Combine(Directory.GetParent(toolsPath)!.FullName, "unity", "classdata.tpk");
        services.AddSingleton<IUnityResourceReader>(_ => new UnityResourceReader(classDataPath));
        services.AddSingleton<IChartRenderAssetManifestBuilder>(_ => new ChartRenderAssetManifestBuilder(classDataPath));
        services.AddSingleton<IChartRenderDescriptorBuilder, ChartRenderDescriptorBuilder>();
        services.AddSingleton<IResourceExporter, ResourceExporter>();
        services.AddSingleton<ILibraryIndex, SqliteLibraryIndex>();
        services.AddSingleton<IAudioPreviewService>(_ => new VgmstreamAudioPreviewService(Path.Combine(toolsPath, "vgmstream-cli.exe")));
        services.AddSingleton<ILibraryScanner, LibraryScanner>();
        return services;
    }
}
