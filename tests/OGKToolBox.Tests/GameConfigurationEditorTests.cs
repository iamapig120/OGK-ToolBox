using System.Text;
using OGKToolBox.Core.Models;
using OGKToolBox.Infrastructure.Configuration;

namespace OGKToolBox.Tests;

public sealed class GameConfigurationEditorTests
{
    [Fact]
    public async Task ClearingAimedbPreviewsItAsACommentedOption()
    {
        using var temp = new TempDirectory();
        var token = TestContext.Current.CancellationToken;
        var path = Path.Combine(temp.Path, "segatools.ini");
        const string original = "[dns]\r\naimedb=https://aime.example\r\ndefault=https://server.example\r\n";
        await File.WriteAllTextAsync(path, original, token);
        var installation = new GameInstallation(temp.Path);
        var snapshot = await new GameConfigurationInspector().InspectAsync(installation, token);
        var file = Assert.Single(snapshot.Files, item => item.Kind == GameConfigurationFileKind.SegaTools);
        var aimedb = Assert.Single(file.Entries, item => item.Section == "dns" && item.Key == "aimedb");
        var edit = new ConfigurationEdit(aimedb.LineNumber, aimedb.Locator, aimedb.Value, string.Empty,
            aimedb.Section, aimedb.Key, Remove: true);
        var editor = new GameConfigurationEditor();

        var preview = await editor.PreviewAsync(installation, file.Kind, [edit], file.ContentHash, token);
        Assert.True(preview.CanSave);
        var change = Assert.Single(preview.Changes);
        Assert.Equal("aimedb", change.Key);
        Assert.Equal("已注释", change.NewValue);
        await editor.SaveAsync(installation, preview, [edit], Path.Combine(temp.Path, "backups"), token);
        Assert.Equal("[dns]\r\n;AimeDB=\r\ndefault=https://server.example\r\n", await File.ReadAllTextAsync(path, token));
    }

    [Fact]
    public async Task ClearingKeychipPreviewsAndSavesItAsACommentedOption()
    {
        using var temp = new TempDirectory();
        var token = TestContext.Current.CancellationToken;
        var path = Path.Combine(temp.Path, "segatools.ini");
        const string original = "[keychip]\r\nid=ABCD1234\r\nsubnet=192.168.123.0\r\n";
        await File.WriteAllTextAsync(path, original, token);
        var installation = new GameInstallation(temp.Path);
        var snapshot = await new GameConfigurationInspector().InspectAsync(installation, token);
        var file = Assert.Single(snapshot.Files, item => item.Kind == GameConfigurationFileKind.SegaTools);
        var keychip = Assert.Single(file.Entries, item => item.Section == "keychip" && item.Key == "id");
        var edit = new ConfigurationEdit(keychip.LineNumber, keychip.Locator, keychip.Value, string.Empty,
            keychip.Section, keychip.Key);
        var editor = new GameConfigurationEditor();

        var preview = await editor.PreviewAsync(installation, file.Kind, [edit], file.ContentHash, token);
        Assert.True(preview.CanSave);
        var change = Assert.Single(preview.Changes);
        Assert.Equal("id", change.Key);
        Assert.Equal("已注释", change.NewValue);
        await editor.SaveAsync(installation, preview, [edit], Path.Combine(temp.Path, "backups"), token);

        Assert.Equal("[keychip]\r\n;id=\r\nsubnet=192.168.123.0\r\n", await File.ReadAllTextAsync(path, token));
    }

    [Fact]
    public async Task PreviewsBacksUpAndAtomicallySavesIniWithoutChangingSurroundingText()
    {
        using var temp = new TempDirectory();
        var token = TestContext.Current.CancellationToken;
        var path = Path.Combine(temp.Path, "segatools.ini");
        const string original = "; header\r\n[system]\r\nfreeplay = 0  ; keep this comment\r\nfuture = untouched\r\n";
        await File.WriteAllTextAsync(path, original, new UTF8Encoding(true), token);
        var inspector = new GameConfigurationInspector();
        var installation = new GameInstallation(temp.Path);
        var snapshot = await inspector.InspectAsync(installation, token);
        var file = Assert.Single(snapshot.Files, item => item.Kind == GameConfigurationFileKind.SegaTools);
        var entry = Assert.Single(file.Entries, item => item.Key == "freeplay");
        var edit = new ConfigurationEdit(entry.LineNumber, entry.Locator, entry.Value, "1");
        var editor = new GameConfigurationEditor();

        var preview = await editor.PreviewAsync(installation, file.Kind, [edit], file.ContentHash, token);
        Assert.True(preview.CanSave);
        Assert.Single(preview.Changes);
        var result = await editor.SaveAsync(installation, preview, [edit],
            Path.Combine(temp.Path, "backups"), token);

        Assert.True(File.Exists(result.BackupPath));
        Assert.Equal(original, await File.ReadAllTextAsync(result.BackupPath, token));
        var saved = await File.ReadAllTextAsync(path, token);
        Assert.Contains("freeplay = 1  ; keep this comment", saved);
        Assert.Contains("future = untouched", saved);
        Assert.Contains("; header", saved);
        Assert.True(File.Exists(Path.Combine(temp.Path, "backups",
            Directory.GetParent(Directory.GetParent(result.BackupPath)!.FullName)!.Name, "operations.jsonl")));
    }

    [Fact]
    public async Task RejectsConcurrentModificationAndJsonEditing()
    {
        using var temp = new TempDirectory();
        var token = TestContext.Current.CancellationToken;
        await File.WriteAllTextAsync(Path.Combine(temp.Path, "mu3.ini"), "[Sound]\nWasapiExclusive=0\n", token);
        await File.WriteAllTextAsync(Path.Combine(temp.Path, "config_client.json"), "{\"enabled\":true}", token);
        var installation = new GameInstallation(temp.Path);
        var snapshot = await new GameConfigurationInspector().InspectAsync(installation, token);
        var ini = Assert.Single(snapshot.Files, file => file.Kind == GameConfigurationFileKind.Mu3);
        var entry = Assert.Single(ini.Entries, item => item.Section == "Sound" && item.Key == "WasapiExclusive");
        await File.AppendAllTextAsync(ini.Path, "; external change\n", token);
        var editor = new GameConfigurationEditor();

        var concurrent = await editor.PreviewAsync(installation, ini.Kind,
            [new(entry.LineNumber, entry.Locator, entry.Value, "1")], ini.ContentHash, token);
        Assert.False(concurrent.CanSave);
        Assert.Contains(concurrent.ValidationErrors, error => error.Contains("其他程序"));

        var json = Assert.Single(snapshot.Files, file => file.Kind == GameConfigurationFileKind.ConfigClient);
        var jsonPreview = await editor.PreviewAsync(installation, json.Kind,
            [new(0, "/enabled", "true", "false")], json.ContentHash, token);
        Assert.False(jsonPreview.CanSave);
        Assert.Contains(jsonPreview.ValidationErrors, error => error.Contains("JSON 配置目前只读"));
    }

    [Fact]
    public async Task AddsMissingSegatoolsOptionWithoutTouchingMu3()
    {
        using var temp = new TempDirectory();
        var token = TestContext.Current.CancellationToken;
        var segatoolsPath = Path.Combine(temp.Path, "segatools.ini");
        var mu3Path = Path.Combine(temp.Path, "mu3.ini");
        await File.WriteAllTextAsync(segatoolsPath, "[vfs]\namfs=amfs\noption=option\nappdata=appdata\n", token);
        const string mu3 = "[AM]\nIgnoreError=0\n";
        await File.WriteAllTextAsync(mu3Path, mu3, token);

        var installation = new GameInstallation(temp.Path);
        var snapshot = await new GameConfigurationInspector().InspectAsync(installation, token);
        var segaTools = Assert.Single(snapshot.Files, file => file.Kind == GameConfigurationFileKind.SegaTools);
        var scan = Assert.Single(segaTools.Entries, entry => entry.Section == "aime" && entry.Key == "scan");
        var edit = new ConfigurationEdit(scan.LineNumber, scan.Locator, scan.Value, "0x0D", scan.Section, scan.Key);
        var editor = new GameConfigurationEditor();

        var preview = await editor.PreviewAsync(installation, segaTools.Kind, [edit], segaTools.ContentHash, token);
        Assert.True(preview.CanSave);
        await editor.SaveAsync(installation, preview, [edit], Path.Combine(temp.Path, "backups"), token);

        Assert.Contains("[aime]", await File.ReadAllTextAsync(segatoolsPath, token));
        Assert.Contains("scan=0x0D", await File.ReadAllTextAsync(segatoolsPath, token));
        Assert.Equal(mu3, await File.ReadAllTextAsync(mu3Path, token));
    }

    [Fact]
    public async Task RejectsInvalidTypedIniValue()
    {
        using var temp = new TempDirectory();
        var token = TestContext.Current.CancellationToken;
        await File.WriteAllTextAsync(Path.Combine(temp.Path, "segatools.ini"),
            "[system]\nfreeplay=0\n", token);
        var installation = new GameInstallation(temp.Path);
        var snapshot = await new GameConfigurationInspector().InspectAsync(installation, token);
        var file = Assert.Single(snapshot.Files, item => item.Kind == GameConfigurationFileKind.SegaTools);
        var entry = Assert.Single(file.Entries, item => item.Section == "system" && item.Key == "freeplay");

        var preview = await new GameConfigurationEditor().PreviewAsync(installation, file.Kind,
            [new(entry.LineNumber, entry.Locator, entry.Value, "sometimes")], file.ContentHash, token);

        Assert.False(preview.CanSave);
        Assert.Contains(preview.ValidationErrors, error => error.Contains("布尔值"));
    }

    [Fact]
    public async Task DoesNotAddOrModifyPcbidConfiguration()
    {
        using var temp = new TempDirectory();
        var token = TestContext.Current.CancellationToken;
        await File.WriteAllTextAsync(Path.Combine(temp.Path, "segatools.ini"),
            "[pcbid]\nserialNo=ACAE01A99999999\n", token);
        var installation = new GameInstallation(temp.Path);
        var snapshot = await new GameConfigurationInspector().InspectAsync(installation, token);
        var file = Assert.Single(snapshot.Files, item => item.Kind == GameConfigurationFileKind.SegaTools);
        var entry = Assert.Single(file.Entries, item => item.Section == "pcbid" && item.Key == "serialNo");

        var preview = await new GameConfigurationEditor().PreviewAsync(installation, file.Kind,
            [new(entry.LineNumber, entry.Locator, entry.Value, "DIFFERENT", entry.Section, entry.Key)],
            file.ContentHash, token);

        Assert.False(preview.CanSave);
        Assert.Contains(preview.ValidationErrors, error => error.Contains("[pcbid]"));

        await File.WriteAllTextAsync(Path.Combine(temp.Path, "segatools.ini"), "[system]\nfreeplay=0\n", token);
        snapshot = await new GameConfigurationInspector().InspectAsync(installation, token);
        file = Assert.Single(snapshot.Files, item => item.Kind == GameConfigurationFileKind.SegaTools);
        Assert.DoesNotContain(file.Entries, item => item.Section == "pcbid" && item.Key == "serialNo");
    }

    [Fact]
    public async Task AddsOnlyOptionsForEnabledModsToMu3Ini()
    {
        using var temp = new TempDirectory();
        var token = TestContext.Current.CancellationToken;
        await File.WriteAllTextAsync(Path.Combine(temp.Path, "mu3.ini"), "[Sound]\nWasapiExclusive=0\n", token);
        var monomod = Path.Combine(temp.Path, "BepInEx", "monomod");
        Directory.CreateDirectory(monomod);
        await File.WriteAllBytesAsync(Path.Combine(monomod, "Assembly-CSharp.FrameRate.mm.dll"), [1, 2, 3], token);
        await File.WriteAllBytesAsync(Path.Combine(monomod, "Assembly-CSharp.SelectBGM.mm.dll.disabled"), [4, 5, 6], token);

        var installation = new GameInstallation(temp.Path);
        var snapshot = await new GameConfigurationInspector().InspectAsync(installation, token);
        var ini = Assert.Single(snapshot.Files, file => file.Kind == GameConfigurationFileKind.Mu3);
        var frameRate = Assert.Single(ini.Entries, entry => entry.Section == "Video" && entry.Key == "Framerate");
        Assert.False(frameRate.IsPresent);
        Assert.Equal("-1", frameRate.DefaultValue);
        Assert.DoesNotContain(ini.Entries, entry => entry.Section == "Extra" && entry.Key == "BGM");

        var edit = new ConfigurationEdit(frameRate.LineNumber, frameRate.Locator, frameRate.Value, "120",
            frameRate.Section, frameRate.Key);
        var editor = new GameConfigurationEditor();
        var preview = await editor.PreviewAsync(installation, ini.Kind, [edit], ini.ContentHash, token);
        Assert.True(preview.CanSave);
        await editor.SaveAsync(installation, preview, [edit], Path.Combine(temp.Path, "backups"), token);

        var content = await File.ReadAllTextAsync(ini.Path, token);
        Assert.Contains("[Video]", content);
        Assert.Contains("Framerate=120", content);
    }

    [Fact]
    public async Task RejectsMu3OptionWhenRequiredModIsDisabled()
    {
        using var temp = new TempDirectory();
        var token = TestContext.Current.CancellationToken;
        var mu3Path = Path.Combine(temp.Path, "mu3.ini");
        await File.WriteAllTextAsync(mu3Path, "[Extra]\n", token);
        var plugins = Path.Combine(temp.Path, "BepInEx", "plugins");
        Directory.CreateDirectory(plugins);
        await File.WriteAllBytesAsync(Path.Combine(plugins, "SelectBGM.dll.disabled"), [1, 2, 3], token);

        var installation = new GameInstallation(temp.Path);
        var snapshot = await new GameConfigurationInspector().InspectAsync(installation, token);
        var mu3 = Assert.Single(snapshot.Files, file => file.Kind == GameConfigurationFileKind.Mu3);
        var editor = new GameConfigurationEditor();
        var preview = await editor.PreviewAsync(installation, mu3.Kind,
            [new(0, "Extra:BGM", string.Empty, "1", "Extra", "BGM")], mu3.ContentHash, token);

        Assert.False(preview.CanSave);
        Assert.Contains(preview.ValidationErrors, error => error.Contains("SelectBGM"));
        Assert.Equal("[Extra]\n", await File.ReadAllTextAsync(mu3Path, token));
    }

    [Fact]
    public async Task RejectsDisableGpValuesOutsideSupportedRange()
    {
        using var temp = new TempDirectory();
        var token = TestContext.Current.CancellationToken;
        var mu3Path = Path.Combine(temp.Path, "mu3.ini");
        await File.WriteAllTextAsync(mu3Path, "[Extra]\nGP=999\n", token);
        var monomod = Path.Combine(temp.Path, "BepInEx", "monomod");
        Directory.CreateDirectory(monomod);
        await File.WriteAllBytesAsync(Path.Combine(monomod, "Assembly-CSharp.DisableGP.mm.dll"), [1, 2, 3], token);

        var installation = new GameInstallation(temp.Path);
        var snapshot = await new GameConfigurationInspector().InspectAsync(installation, token);
        var mu3 = Assert.Single(snapshot.Files, file => file.Kind == GameConfigurationFileKind.Mu3);
        var gp = Assert.Single(mu3.Entries, entry => entry.Section == "Extra" && entry.Key == "GP");
        var preview = await new GameConfigurationEditor().PreviewAsync(installation, mu3.Kind,
            [new(gp.LineNumber, gp.Locator, gp.Value, "1000", gp.Section, gp.Key)], mu3.ContentHash, token);

        Assert.False(preview.CanSave);
        Assert.Contains(preview.ValidationErrors, error => error.Contains("GP 只能填写 0 到 999"));
    }
}
