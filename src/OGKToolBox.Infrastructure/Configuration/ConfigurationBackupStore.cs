using System.Security.Cryptography;
using System.Text.Json;
using OGKToolBox.Application.Abstractions;
using OGKToolBox.Application.Contracts;
using OGKToolBox.Core.Models;

namespace OGKToolBox.Infrastructure.Configuration;

public sealed class ConfigurationBackupStore : IConfigurationBackupStore
{
    public async Task<IReadOnlyList<BackupInfo>> ListAsync(string installationId, GameInstallation installation,
        GameConfigurationFileKind kind, IOpaqueIdService ids, CancellationToken cancellationToken)
    {
        var fileName = FileName(kind);
        if (!Directory.Exists(installation.ConfigurationBackupPath)) return [];
        var files = Directory.EnumerateFiles(installation.ConfigurationBackupPath, "*.bak", SearchOption.AllDirectories)
            .Where(path => string.Equals(new DirectoryInfo(Path.GetDirectoryName(path)!).Name, fileName,
                StringComparison.OrdinalIgnoreCase))
            .Select(path => new FileInfo(path))
            .OrderByDescending(file => file.LastWriteTimeUtc)
            .ToArray();
        var result = new List<BackupInfo>(files.Length);
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relative = Path.GetRelativePath(installation.ConfigurationBackupPath, file.FullName);
            var hash = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(file.FullName, cancellationToken)));
            result.Add(new(ids.Create("backup", installationId, ((int)kind).ToString(), relative), kind,
                new DateTimeOffset(file.LastWriteTimeUtc, TimeSpan.Zero), file.Length, hash));
        }
        return result;
    }

    public async Task<RestoreResult> RestoreAsync(GameInstallation installation, string backupFileName,
        GameConfigurationFileKind kind, CancellationToken cancellationToken)
    {
        var backupRoot = Path.GetFullPath(installation.ConfigurationBackupPath);
        var backupPath = Path.GetFullPath(Path.Combine(backupRoot, backupFileName));
        if (!IsInside(backupPath, backupRoot) || !File.Exists(backupPath))
            throw new FileNotFoundException("The selected configuration backup is unavailable.");
        EnsureNoReparsePoints(backupRoot, backupPath);

        var target = Path.Combine(installation.RootPath, RelativeTarget(kind));
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        EnsureNoReparsePoints(Path.GetFullPath(installation.RootPath), target);
        var bytes = await File.ReadAllBytesAsync(backupPath, cancellationToken);
        var hash = Convert.ToHexString(SHA256.HashData(bytes));
        var temporary = target + $".ogk-restore-{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write,
                             FileShare.None, 4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(bytes, cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }
            File.Move(temporary, target, true);
            var restoredHash = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(target, cancellationToken)));
            if (!CryptographicOperations.FixedTimeEquals(Convert.FromHexString(hash), Convert.FromHexString(restoredHash)))
                throw new IOException("The restored configuration did not match the selected backup.");
            var result = new RestoreResult(kind, restoredHash, DateTimeOffset.Now);
            await AppendLogAsync(installation, new
            {
                action = "restore",
                kind = kind.ToString(),
                backup = Path.GetFileName(backupPath),
                result.ContentHash,
                result.RestoredAt
            }, cancellationToken);
            return result;
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    public Task TrimAsync(GameInstallation installation, GameConfigurationFileKind kind, int retain,
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(installation.ConfigurationBackupPath)) return Task.CompletedTask;
        var fileName = FileName(kind);
        foreach (var obsolete in Directory.EnumerateFiles(installation.ConfigurationBackupPath, "*.bak", SearchOption.AllDirectories)
                     .Where(path => string.Equals(new DirectoryInfo(Path.GetDirectoryName(path)!).Name, fileName,
                         StringComparison.OrdinalIgnoreCase))
                     .Select(path => new FileInfo(path)).OrderByDescending(file => file.LastWriteTimeUtc).Skip(retain))
        {
            cancellationToken.ThrowIfCancellationRequested();
            obsolete.Delete();
        }
        return Task.CompletedTask;
    }

    private static string FileName(GameConfigurationFileKind kind) => Path.GetFileName(RelativeTarget(kind));

    private static string RelativeTarget(GameConfigurationFileKind kind) => kind switch
    {
        GameConfigurationFileKind.SegaTools => "segatools.ini",
        GameConfigurationFileKind.Mu3 => "mu3.ini",
        GameConfigurationFileKind.BepInEx => Path.Combine("BepInEx", "config", "BepInEx.cfg"),
        GameConfigurationFileKind.ConfigClient => "config_client.json",
        GameConfigurationFileKind.ConfigCommon => "config_common.json",
        GameConfigurationFileKind.ConfigServer => "config_server.json",
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    private static bool IsInside(string path, string root)
    {
        var relative = Path.GetRelativePath(root, path);
        return relative.Length > 0 && relative != "." && !Path.IsPathRooted(relative)
            && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }

    private static void EnsureNoReparsePoints(string root, string path)
    {
        var current = new FileInfo(path);
        if (current.Exists && current.Attributes.HasFlag(FileAttributes.ReparsePoint))
            throw new UnauthorizedAccessException("Configuration links cannot be used for backup restoration.");
        var directory = current.Directory;
        while (directory is not null)
        {
            if (directory.Exists && directory.Attributes.HasFlag(FileAttributes.ReparsePoint))
                throw new UnauthorizedAccessException("Configuration links cannot be used for backup restoration.");
            if (directory.FullName.Equals(root, StringComparison.OrdinalIgnoreCase)) return;
            directory = directory.Parent;
        }
        throw new UnauthorizedAccessException("The configuration path is outside the selected installation.");
    }

    private static async Task AppendLogAsync(GameInstallation installation, object record, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(installation.LogPath);
        await File.AppendAllTextAsync(Path.Combine(installation.LogPath, "configuration-operations.jsonl"),
            JsonSerializer.Serialize(record) + Environment.NewLine, cancellationToken);
    }
}
