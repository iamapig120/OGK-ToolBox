using System.IO;
using System.Text.Json;

namespace OGKToolBox.App.Services;

public sealed record AppPaths(string LocalDataPath)
{
    public string SettingsPath => Path.Combine(LocalDataPath, "settings.json");
    public string AudioCachePath => Path.Combine(LocalDataPath, "cache", "audio");
    public string ChartSoundCachePath => Path.Combine(LocalDataPath, "cache", "chart-sounds");
    public string ConfigurationBackupPath => Path.Combine(LocalDataPath, "backups", "configuration");
    public string ModOperationPath => Path.Combine(LocalDataPath, "operations", "mods");
}

public enum AppTheme { Light, Dark, System }

public sealed class AppSettings
{
    public int SettingsVersion { get; set; } = 3;
    public string GameRoot { get; set; } = string.Empty;
    public AppTheme Theme { get; set; } = AppTheme.Light;
    public double ChartScrollSpeed { get; set; } = 6d;
    public double UiScale { get; set; } = 1d;
}

public sealed class SettingsService(AppPaths paths)
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };
    private readonly SemaphoreSlim _saveGate = new(1, 1);
    public AppSettings Current { get; private set; } = new();

    public async Task LoadAsync()
    {
        Directory.CreateDirectory(paths.LocalDataPath);
        if (!File.Exists(paths.SettingsPath)) return;
        var migrated = false;
        try
        {
            var json = await File.ReadAllTextAsync(paths.SettingsPath);
            Current = JsonSerializer.Deserialize<AppSettings>(json, Options) ?? new();
            using var document = JsonDocument.Parse(json);
            var hasVersion = document.RootElement.TryGetProperty(nameof(AppSettings.SettingsVersion), out _);
            if (!hasVersion || Current.SettingsVersion < 2)
            {
                Current.ChartScrollSpeed = 6d;
                migrated = true;
            }
            if (!hasVersion || Current.SettingsVersion < 3)
            {
                Current.UiScale = 1d;
                migrated = true;
            }
            var normalizedUiScale = NormalizeUiScale(Current.UiScale);
            if (Math.Abs(Current.UiScale - normalizedUiScale) >= 0.001)
            {
                Current.UiScale = normalizedUiScale;
                migrated = true;
            }
            Current.SettingsVersion = 3;
        }
        catch (JsonException) { Current = new(); }
        if (migrated) await SaveAsync();
    }

    public async Task SaveAsync()
    {
        await _saveGate.WaitAsync();
        try
        {
            Directory.CreateDirectory(paths.LocalDataPath);
            await using var stream = File.Create(paths.SettingsPath);
            await JsonSerializer.SerializeAsync(stream, Current, Options);
        }
        finally { _saveGate.Release(); }
    }

    private static double NormalizeUiScale(double value)
    {
        double[] supported = [1d, 1.25d, 1.5d, 1.75d, 2d];
        return supported.MinBy(scale => Math.Abs(scale - value));
    }
}
