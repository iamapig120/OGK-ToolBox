using System.Text;
using OGKToolBox.Core.Models;
using OGKToolBox.Infrastructure.Configuration;

namespace OGKToolBox.Tests;

public sealed class ModManagerTests
{
    [Fact]
    public async Task DisablesAndReenablesMonoModByReversibleRename()
    {
        using var temp = new TempDirectory();
        var token = TestContext.Current.CancellationToken;
        var monomod = temp.CreateDirectory("BepInEx", "monomod");
        var originalPath = Path.Combine(monomod, "Assembly-CSharp.UnlockTest.mm.dll");
        var originalBytes = Encoding.UTF8.GetBytes("test mod bytes");
        await File.WriteAllBytesAsync(originalPath, originalBytes, token);
        var installation = new GameInstallation(temp.Path);
        var inspector = new GameConfigurationInspector();
        var manager = new ModManager(() => null);
        var operationRoot = Path.Combine(temp.Path, "operations");

        var initial = await inspector.InspectAsync(installation, token);
        var enabled = Assert.Single(initial.Mods);
        Assert.Equal("UnlockTest", enabled.Name);
        Assert.True(enabled.IsEnabled);
        var disablePreview = await manager.PreviewToggleAsync(installation, enabled, token);
        Assert.True(disablePreview.CanApply);
        Assert.EndsWith(".mm.dll.disabled", disablePreview.TargetPath, StringComparison.OrdinalIgnoreCase);
        var disabledResult = await manager.ApplyToggleAsync(installation, disablePreview, operationRoot, token);
        Assert.False(disabledResult.IsEnabled);
        Assert.False(File.Exists(originalPath));
        Assert.Equal(originalBytes, await File.ReadAllBytesAsync(disabledResult.Path, token));

        var afterDisable = await inspector.InspectAsync(installation, token);
        var disabled = Assert.Single(afterDisable.Mods);
        Assert.Equal("UnlockTest", disabled.Name);
        Assert.False(disabled.IsEnabled);
        var enablePreview = await manager.PreviewToggleAsync(installation, disabled, token);
        var enabledResult = await manager.ApplyToggleAsync(installation, enablePreview, operationRoot, token);

        Assert.True(enabledResult.IsEnabled);
        Assert.Equal(originalPath, enabledResult.Path, ignoreCase: true);
        Assert.Equal(2, (await File.ReadAllLinesAsync(Path.Combine(operationRoot, "operations.jsonl"), token)).Length);
    }

    [Fact]
    public async Task TogglesBepInExPluginAndRejectsDestinationCollision()
    {
        using var temp = new TempDirectory();
        var token = TestContext.Current.CancellationToken;
        var plugins = temp.CreateDirectory("BepInEx", "plugins");
        var pluginPath = Path.Combine(plugins, "Example.Plugin.dll");
        await File.WriteAllBytesAsync(pluginPath, [1, 2, 3], token);
        var installation = new GameInstallation(temp.Path);
        var inspector = new GameConfigurationInspector();
        var manager = new ModManager(() => null);
        var plugin = Assert.Single((await inspector.InspectAsync(installation, token)).Mods);

        await File.WriteAllBytesAsync(pluginPath + ".disabled", [4, 5, 6], token);
        var preview = await manager.PreviewToggleAsync(installation, plugin, token);

        Assert.False(preview.CanApply);
        Assert.Contains(preview.Errors, error => error.Contains("目标文件已存在"));
        Assert.True(File.Exists(pluginPath));
    }

    [Fact]
    public async Task RejectsChangedFileAndRunningGameBeforeRename()
    {
        using var temp = new TempDirectory();
        var token = TestContext.Current.CancellationToken;
        var monomod = temp.CreateDirectory("BepInEx", "monomod");
        var path = Path.Combine(monomod, "Assembly-CSharp.Test.mm.dll");
        await File.WriteAllBytesAsync(path, [1, 2, 3], token);
        var installation = new GameInstallation(temp.Path);
        var mod = Assert.Single((await new GameConfigurationInspector().InspectAsync(installation, token)).Mods);
        var manager = new ModManager(() => null);
        var preview = await manager.PreviewToggleAsync(installation, mod, token);
        await File.AppendAllTextAsync(path, "changed", token);

        await Assert.ThrowsAsync<IOException>(() => manager.ApplyToggleAsync(
            installation, preview, Path.Combine(temp.Path, "operations"), token));
        Assert.True(File.Exists(path));

        var runningManager = new ModManager(() => "mu3");
        var refreshed = Assert.Single((await new GameConfigurationInspector().InspectAsync(installation, token)).Mods);
        var runningPreview = await runningManager.PreviewToggleAsync(installation, refreshed, token);
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => runningManager.ApplyToggleAsync(
            installation, runningPreview, Path.Combine(temp.Path, "operations"), token));
        Assert.Contains("mu3.exe", exception.Message);
    }
}
