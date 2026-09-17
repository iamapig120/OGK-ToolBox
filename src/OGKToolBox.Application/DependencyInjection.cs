using Microsoft.Extensions.DependencyInjection;
using OGKToolBox.Application.Abstractions;
using OGKToolBox.Application.Services;

namespace OGKToolBox.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddOGKToolBoxApplication(this IServiceCollection services, byte[] idSecret)
    {
        services.AddSingleton<IOpaqueIdService>(_ => new OpaqueIdService(idSecret));
        services.AddSingleton<IInstallationRegistry, InstallationRegistry>();
        services.AddSingleton<ILibraryApplicationService, LibraryApplicationService>();
        services.AddSingleton<IConfigurationApplicationService, ConfigurationApplicationService>();
        services.AddSingleton<IResourceApplicationService, ResourceApplicationService>();
        services.AddSingleton<IOptionPackageApplicationService, OptionPackageApplicationService>();
        return services;
    }
}
