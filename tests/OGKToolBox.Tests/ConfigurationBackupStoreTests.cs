using System.Security.Cryptography;
using OGKToolBox.Application.Services;
using OGKToolBox.Core.Models;
using OGKToolBox.Infrastructure.Configuration;

namespace OGKToolBox.Tests;

public sealed class ConfigurationBackupStoreTests
{
    [Fact]
    public async Task RestoreReplacesExactlyWithoutBackingUpTheCurrentFile()
    {
        using var temp = new TempDirectory();
        var token = TestContext.Current.CancellationToken;
        var installation = new GameInstallation(temp.Path);
        var backupDirectory = Path.Combine(installation.ConfigurationBackupPath, "installation", "segatools.ini");
        Directory.CreateDirectory(backupDirectory);
        var backup = Path.Combine(backupDirectory, "20260814-010203-000.bak");
        var expected = "[system]\r\nfreeplay=1\r\n"u8.ToArray();
        await File.WriteAllBytesAsync(backup, expected, token);
        await File.WriteAllTextAsync(Path.Combine(temp.Path, "segatools.ini"), "current content", token);
        var before = Directory.GetFiles(installation.ConfigurationBackupPath, "*.bak", SearchOption.AllDirectories);
        var store = new ConfigurationBackupStore();

        var result = await store.RestoreAsync(installation,
            Path.GetRelativePath(installation.ConfigurationBackupPath, backup), GameConfigurationFileKind.SegaTools, token);

        Assert.Equal(expected, await File.ReadAllBytesAsync(Path.Combine(temp.Path, "segatools.ini"), token));
        Assert.Equal(Convert.ToHexString(SHA256.HashData(expected)), result.ContentHash);
        Assert.Equal(before, Directory.GetFiles(installation.ConfigurationBackupPath, "*.bak", SearchOption.AllDirectories));
        Assert.Empty(Directory.GetFiles(temp.Path, "*.ogk-restore-*.tmp", SearchOption.TopDirectoryOnly));
    }

    [Fact]
    public async Task RetentionKeepsNewestTwentyBackups()
    {
        using var temp = new TempDirectory();
        var installation = new GameInstallation(temp.Path);
        var directory = Path.Combine(installation.ConfigurationBackupPath, "installation", "segatools.ini");
        Directory.CreateDirectory(directory);
        for (var index = 0; index < 25; index++)
        {
            var file = Path.Combine(directory, $"{index:D2}.bak");
            await File.WriteAllTextAsync(file, index.ToString(), TestContext.Current.CancellationToken);
            File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddMinutes(index));
        }

        await new ConfigurationBackupStore().TrimAsync(installation, GameConfigurationFileKind.SegaTools, 20,
            TestContext.Current.CancellationToken);

        var remaining = Directory.GetFiles(directory, "*.bak").Select(Path.GetFileName).Order().ToArray();
        Assert.Equal(20, remaining.Length);
        Assert.DoesNotContain("00.bak", remaining);
        Assert.Contains("24.bak", remaining);
    }
}
