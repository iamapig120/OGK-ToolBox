using System.Text;
using OGKToolBox.Core.Models;
using OGKToolBox.Infrastructure.Configuration;

namespace OGKToolBox.Tests;

public sealed class GameConfigurationInspectorTests
{
    [Fact]
    public async Task ReadsKnownAndUnknownFieldsMasksIdentifiersAndDiscoversMods()
    {
        using var temp = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        Directory.CreateDirectory(Path.Combine(temp.Path, "BepInEx", "config"));
        Directory.CreateDirectory(Path.Combine(temp.Path, "BepInEx", "monomod"));
        Directory.CreateDirectory(Path.Combine(temp.Path, "BepInEx", "plugins"));
        Directory.CreateDirectory(Path.Combine(temp.Path, "BepInEx", "plugins", "Nested"));
        await File.WriteAllTextAsync(Path.Combine(temp.Path, "segatools.ini"),
            "[keychip]\r\nid=A12345678901\r\n[custom]\r\nfutureOption=keep-me\r\n", new UTF8Encoding(true), cancellationToken);
        await File.WriteAllTextAsync(Path.Combine(temp.Path, "mu3.ini"),
            "[Sound]\r\nWasapiExclusive=1\r\n", cancellationToken);
        await File.WriteAllTextAsync(Path.Combine(temp.Path, "BepInEx", "config", "BepInEx.cfg"),
            "[Logging.Console]\r\nEnabled = true\r\n", cancellationToken);
        await File.WriteAllBytesAsync(Path.Combine(temp.Path, "BepInEx", "monomod",
            "Assembly-CSharp.UnlockAllMusic.mm.dll"), [0, 1, 2], cancellationToken);
        await File.WriteAllBytesAsync(Path.Combine(temp.Path, "BepInEx", "monomod",
            "Assembly-CSharp.AttractVideoPlayer.mm.dll"), [0, 1, 2], cancellationToken);
        await File.WriteAllBytesAsync(Path.Combine(temp.Path, "BepInEx", "monomod",
            "Assembly-CSharp.DisableGP.mm.dll"), [0, 1, 2], cancellationToken);
        await File.WriteAllBytesAsync(Path.Combine(temp.Path, "BepInEx", "plugins",
            "Example.Plugin.dll.disabled"), [0, 1, 2], cancellationToken);
        await File.WriteAllBytesAsync(Path.Combine(temp.Path, "BepInEx", "plugins", "Nested",
            "Nested.Plugin.dll"), [0, 1, 2], cancellationToken);

        var snapshot = await new GameConfigurationInspector().InspectAsync(
            new GameInstallation(temp.Path), cancellationToken);

        Assert.Equal(6, snapshot.Files.Count);
        var segaTools = Assert.Single(snapshot.Files, file => file.Kind == GameConfigurationFileKind.SegaTools);
        var identifier = Assert.Single(segaTools.Entries, entry => entry.Key == "id");
        Assert.True(identifier.IsKnown);
        Assert.True(identifier.IsSensitive);
        Assert.Equal("A12345678901", identifier.Value);
        Assert.NotEqual(identifier.Value, identifier.DisplayValue);
        Assert.DoesNotContain("123456", identifier.DisplayValue);
        Assert.Contains(segaTools.Entries, entry => entry.Key == "futureOption" && !entry.IsKnown);
        var keyboard = Assert.Single(segaTools.Entries,
            entry => entry.Section == "io4" && entry.Key == "keyboard");
        Assert.False(keyboard.IsPresent);
        Assert.Equal("1", keyboard.DefaultValue);

        Assert.Contains(snapshot.Mods, mod => mod.Name == "UnlockAllMusic"
            && mod.Kind == InstalledModKind.MonoModPatch && mod.IsEnabled);
        Assert.Contains(snapshot.Mods, mod => mod.Name == "Example.Plugin"
            && mod.Kind == InstalledModKind.BepInExPlugin && !mod.IsEnabled);
        Assert.DoesNotContain(snapshot.Mods, mod => mod.Name == "Nested.Plugin");
        Assert.Contains(snapshot.Mods, mod => mod.Name == "AttractVideoPlayer"
            && mod.NameText == "禁用待机时视频展示");
        Assert.Contains(snapshot.Mods, mod => mod.Name == "DisableGP"
            && mod.NameText == "移除投币和更改 GP");
    }

    [Fact]
    public async Task FlattensAmDaemonJsonFilesIntoTypedPaths()
    {
        using var temp = new TempDirectory();
        var token = TestContext.Current.CancellationToken;
        await File.WriteAllTextAsync(Path.Combine(temp.Path, "config_common.json"),
            "{\r\n  \"network\": { \"enable\": true, \"port\": 80 },\r\n  \"input\": { \"players\": [1, 2] }\r\n}", token);

        var snapshot = await new GameConfigurationInspector().InspectAsync(
            new GameInstallation(temp.Path), token);

        var common = Assert.Single(snapshot.Files, file => file.Kind == GameConfigurationFileKind.ConfigCommon);
        Assert.True(common.Exists);
        Assert.Equal("CRLF", common.NewLineName);
        Assert.Contains(common.Entries, entry => entry.Locator == "/network/enable"
            && entry.ValueKind == ConfigurationValueKind.Boolean && entry.Value == "true");
        Assert.Contains(common.Entries, entry => entry.Locator == "/network/port"
            && entry.ValueKind == ConfigurationValueKind.Integer && entry.Value == "80");
        Assert.Contains(common.Entries, entry => entry.Locator == "/input/players"
            && entry.Value == "[1, 2]");
    }

    [Fact]
    public async Task ReportsMissingOptionalConfigurationWithoutThrowing()
    {
        using var temp = new TempDirectory();

        var snapshot = await new GameConfigurationInspector().InspectAsync(
            new GameInstallation(temp.Path), TestContext.Current.CancellationToken);

        Assert.Empty(snapshot.Mods);
        Assert.Contains(snapshot.Diagnostics, item => item.Code == "SEGATOOLS_CONFIG_MISSING");
        Assert.Contains(snapshot.Diagnostics, item => item.Code == "BEPINEX_NOT_INSTALLED");
    }

    [Fact]
    public async Task AddsMissingVanillaMu3OptionsAsEditableEntries()
    {
        using var temp = new TempDirectory();
        var token = TestContext.Current.CancellationToken;
        await File.WriteAllTextAsync(Path.Combine(temp.Path, "mu3.ini"), "[AM]\nIgnoreError=0\n", token);

        var snapshot = await new GameConfigurationInspector().InspectAsync(new GameInstallation(temp.Path), token);
        var mu3 = Assert.Single(snapshot.Files, file => file.Kind == GameConfigurationFileKind.Mu3);
        var optionDev = Assert.Single(mu3.Entries, entry => entry.Section == "AM" && entry.Key == "OptionDev");
        Assert.False(optionDev.IsPresent);
        Assert.Equal("0", optionDev.DefaultValue);
        Assert.Empty(optionDev.RequiredModsOrEmpty);
    }

    [Fact]
    public async Task AddsMissingSegatoolsOptionsAndReportsRequiredVfsPaths()
    {
        using var temp = new TempDirectory();
        var token = TestContext.Current.CancellationToken;
        await File.WriteAllTextAsync(Path.Combine(temp.Path, "segatools.ini"),
            "[vfs]\namfs=\noption=option\n", token);
        await File.WriteAllTextAsync(Path.Combine(temp.Path, "mu3.ini"), "[AM]\nIgnoreError=0\n", token);

        var snapshot = await new GameConfigurationInspector().InspectAsync(new GameInstallation(temp.Path), token);
        var segaTools = Assert.Single(snapshot.Files, file => file.Kind == GameConfigurationFileKind.SegaTools);
        var portNo = Assert.Single(segaTools.Entries, entry => entry.Section == "aime" && entry.Key == "portNo");
        var scan = Assert.Single(segaTools.Entries, entry => entry.Section == "aime" && entry.Key == "scan");
        var serialPort = Assert.Single(segaTools.Entries, entry => entry.Section == "led" && entry.Key == "serialPort");
        var keychip = Assert.Single(segaTools.Entries, entry => entry.Section == "keychip" && entry.Key == "id");
        var replaceHost = Assert.Single(segaTools.Entries, entry => entry.Section == "dns" && entry.Key == "replaceHost");
        Assert.False(portNo.IsPresent);
        Assert.Equal("0", portNo.DefaultValue);
        Assert.False(scan.IsPresent);
        Assert.Equal("0x0D", scan.DefaultValue);
        Assert.False(serialPort.IsPresent);
        Assert.Equal("COM5", serialPort.DefaultValue);
        Assert.False(keychip.IsPresent);
        Assert.True(keychip.IsSensitive);
        Assert.False(replaceHost.IsPresent);
        Assert.Equal("0", replaceHost.DefaultValue);
        Assert.Equal(2, snapshot.Diagnostics.Count(item => item.Code == "SEGATOOLS_REQUIRED_PATH_MISSING"));

        var mu3 = Assert.Single(snapshot.Files, file => file.Kind == GameConfigurationFileKind.Mu3);
        Assert.DoesNotContain(mu3.Entries, entry => entry.Key is "portNo" or "scan" or "serialPort");
    }
}
