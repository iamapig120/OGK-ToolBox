using System.IO;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OGKToolBox.App.Services;
using OGKToolBox.App.ViewModels;
using OGKToolBox.Infrastructure;

namespace OGKToolBox.App;

public partial class App : System.Windows.Application
{
    private IHost? _host;

    protected override async void OnStartup(StartupEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("WINDIR")))
        {
            Environment.SetEnvironmentVariable("WINDIR", Environment.GetFolderPath(Environment.SpecialFolder.Windows));
        }

        base.OnStartup(e);
        var localData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OGKToolBox");
        _host = Host.CreateDefaultBuilder()
            .ConfigureServices(services =>
            {
                services.AddOGKToolBoxInfrastructure(Path.Combine(AppContext.BaseDirectory, "tools", "vgmstream"));
                services.AddSingleton(new AppPaths(localData));
                services.AddSingleton<SettingsService>();
                services.AddSingleton<ChartSoundEffectService>();
                services.AddSingleton<ThemeService>();
                services.AddSingleton<ConfigurationManagementViewModel>();
                services.AddSingleton<MainViewModel>();
                services.AddSingleton<MainWindow>();
            })
            .Build();
        await _host.StartAsync();
        var settings = _host.Services.GetRequiredService<SettingsService>();
        await settings.LoadAsync();
        _host.Services.GetRequiredService<ThemeService>().Apply(settings.Current.Theme);
        _host.Services.GetRequiredService<MainWindow>().Show();
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        if (_host is not null)
        {
            await _host.StopAsync(TimeSpan.FromSeconds(2));
            _host.Dispose();
        }
        base.OnExit(e);
    }
}
