using OGKToolBox.Application.Services;
using OGKToolBox.Core.Models;
using OGKToolBox.Infrastructure.Configuration;
using OGKToolBox.Infrastructure.Files;
using OGKToolBox.Infrastructure.Scanning;

namespace OGKToolBox.Tests;

public sealed class SegatoolsIo4DefaultsTests
{
    [Theory]
    [InlineData("; keep this comment\r\n[dns]\r\ndefault=example.invalid\r\n", "1")]
    [InlineData("[io4]\r\nkeyboard=0\r\n;enable=0\r\n", "1")]
    [InlineData("[IO4]\r\nEnable=0 ; real controller\r\nkeyboard=0\r\n", "0")]
    [InlineData("[io4]\r\nenable=1\r\n", "1")]
    public async Task AddsOnlyMissingEnableAndPersistsSwitchAcrossInspections(string original, string expected)
    {
        using var temp = new TempDirectory();
        var token = TestContext.Current.CancellationToken;
        var installation = new GameInstallation(temp.Path);
        Directory.CreateDirectory(installation.BaseGameDataPath);
        Directory.CreateDirectory(installation.BaseAssetsPath);
        var registry = new InstallationRegistry(new GameInstallationValidator(), new LocalFileSystem());
        var session = registry.Register(temp.Path);
        var service = new ConfigurationApplicationService(registry, new OpaqueIdService(new byte[32]),
            new GameConfigurationInspector(), new GameConfigurationEditor(), new ConfigurationBackupStore(),
            new ModManager(), new InstallationFileCatalog());
        var path = Path.Combine(temp.Path, "segatools.ini");
        await File.WriteAllTextAsync(path, original, token);

        var view = await service.InspectAsync(session.Id, token);
        var file = Assert.Single(view.Files, item => item.Kind == GameConfigurationFileKind.SegaTools);
        var enable = Assert.Single(file.Entries, item => item.Section.Equals("io4", StringComparison.OrdinalIgnoreCase)
            && item.Key.Equals("enable", StringComparison.OrdinalIgnoreCase));
        Assert.True(enable.IsPresent);
        Assert.True(enable.IsKnown);
        Assert.Equal(ConfigurationValueKind.Boolean, enable.ValueKind);
        Assert.Equal(expected, enable.Value);
        Assert.Equal("1", enable.DefaultValue);
        var first = await File.ReadAllBytesAsync(path, token);
        await service.InspectAsync(session.Id, token);
        Assert.Equal(first, await File.ReadAllBytesAsync(path, token));
        Assert.Contains(Directory.EnumerateFiles(installation.ConfigurationBackupPath, "*", SearchOption.AllDirectories),
            backup => File.ReadAllText(backup) == original);

        foreach (var attempt in Enumerable.Range(0, 3))
        {
            var value = enable.Value == "1" ? "0" : "1";
            var edit = new ConfigurationEdit(enable.LineNumber, enable.Locator, enable.Value, value, enable.Section, enable.Key);
            var preview = await service.PreviewAsync(session.Id, file.Kind, file.ContentHash, [edit], token);
            Assert.True(preview.CanSave, string.Join("; ", preview.ValidationErrors));
            await service.SaveAsync(session.Id, preview.Token, token);
            view = await service.InspectAsync(session.Id, token);
            file = Assert.Single(view.Files, item => item.Kind == GameConfigurationFileKind.SegaTools);
            enable = Assert.Single(file.Entries, item => item.Key.Equals("enable", StringComparison.OrdinalIgnoreCase)
                && item.Section.Equals("io4", StringComparison.OrdinalIgnoreCase));
            Assert.Equal(value, enable.Value);
        }
        var text = await File.ReadAllTextAsync(path, token);
        if (original.Contains("keyboard=0")) Assert.Contains("keyboard=0", text);
        if (original.Contains("; keep")) Assert.Contains("; keep this comment", text);
        if (original.Contains(";enable=0")) Assert.Contains(";enable=0", text);
    }
}
