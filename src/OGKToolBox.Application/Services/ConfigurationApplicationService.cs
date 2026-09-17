using System.Collections.Concurrent;
using OGKToolBox.Application.Abstractions;
using OGKToolBox.Application.Contracts;
using OGKToolBox.Core.Abstractions;
using OGKToolBox.Core.Models;

namespace OGKToolBox.Application.Services;

public sealed class ConfigurationApplicationService(
    IInstallationRegistry installations,
    IOpaqueIdService ids,
    IGameConfigurationInspector inspector,
    IGameConfigurationEditor editor,
    IConfigurationBackupStore backups,
    IModManager mods,
    IInstallationFileCatalog files) : IConfigurationApplicationService
{
    private readonly ConcurrentDictionary<string, PendingPreview> _previews = new(StringComparer.Ordinal);

    public async Task<IoDllCatalog> ListIoDllsAsync(string installationId, CancellationToken cancellationToken)
    {
        var session = installations.GetRequired(installationId);
        return new(session.DisplayName,
            await files.ListRootDllsAsync(session.Installation, cancellationToken));
    }

    public async Task<ConfigurationView> InspectAsync(string installationId, CancellationToken cancellationToken)
    {
        var session = installations.GetRequired(installationId);
        var snapshot = await inspector.InspectAsync(session.Installation, cancellationToken);
        snapshot = await EnsureSegatoolsDefaultsAsync(session.Installation, snapshot, cancellationToken);
        var mu3 = snapshot.Files.FirstOrDefault(file => file.Kind == GameConfigurationFileKind.Mu3);
        return new(installationId, snapshot.HookVersion,
            snapshot.Files.Select(file => new ConfigurationFileView(file.Kind, file.DisplayName, file.Exists,
                file.EncodingName, file.NewLineName, file.LastWriteTime, file.Size, file.Entries, file.ContentHash)).ToArray(),
            snapshot.Mods.Select(mod => new InstalledModView(
                ids.Create("mod", installationId, ((int)mod.Kind).ToString(),
                    Path.GetRelativePath(session.Installation.RootPath, mod.Path)),
                mod.Name, mod.FileName, mod.Kind, mod.IsEnabled, mod.Version, mod.LastWriteTime, mod.Size,
                mod.ChineseName, mod.Description,
                mu3?.Entries.Where(entry => entry.RequiredModsOrEmpty.Contains(mod.Name,
                    StringComparer.OrdinalIgnoreCase)).ToArray() ?? [])).ToArray(),
            snapshot.Diagnostics);
    }

    private async Task<GameConfigurationSnapshot> EnsureSegatoolsDefaultsAsync(
        GameInstallation installation,
        GameConfigurationSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        var file = snapshot.Files.FirstOrDefault(item => item.Kind == GameConfigurationFileKind.SegaTools);
        if (file is null or { Exists: false }) return snapshot;

        var edits = file.Entries
            .Where(entry => !entry.IsPresent && IsAutoAddedSegatoolsOption(entry.Section, entry.Key))
            .Select(entry => new ConfigurationEdit(entry.LineNumber, entry.Locator, entry.Value,
                entry.DefaultValue, entry.Section, entry.Key))
            .ToArray();
        if (edits.Length == 0) return snapshot;

        var preview = await editor.PreviewAsync(installation, file.Kind, edits, file.ContentHash, cancellationToken);
        if (!preview.CanSave) return snapshot;
        try
        {
            await editor.SaveAsync(installation, preview, edits, installation.ConfigurationBackupPath, cancellationToken);
            return await inspector.InspectAsync(installation, cancellationToken);
        }
        catch (InvalidOperationException)
        {
            return snapshot;
        }
        catch (IOException)
        {
            return snapshot;
        }
    }

    private static bool IsAutoAddedSegatoolsOption(string section, string key) =>
        (section, key) is
            ("vfs", "amfs") or ("vfs", "option") or ("vfs", "appdata") or
            ("aime", "portNo") or ("aime", "scan") or
            ("dns", "replaceHost") or
            ("io4", "enable") or ("io4", "keyboard") or
            ("led", "serialPort");

    public async Task<ConfigurationPreviewTicket> PreviewAsync(string installationId, GameConfigurationFileKind kind,
        string baselineHash, IReadOnlyList<ConfigurationEdit> edits, CancellationToken cancellationToken)
    {
        CleanupExpired();
        var session = installations.GetRequired(installationId);
        var preview = await editor.PreviewAsync(session.Installation, kind, edits, baselineHash, cancellationToken);
        var token = Guid.NewGuid().ToString("N");
        var expiresAt = DateTimeOffset.UtcNow.AddMinutes(5);
        if (preview.CanSave) _previews[token] = new(installationId, preview, edits.ToArray(), expiresAt);
        return new(token, preview.CanSave, preview.ValidationErrors, preview.ProposedHash, expiresAt);
    }

    public async Task<ConfigurationSaveInfo> SaveAsync(string installationId, string previewToken,
        CancellationToken cancellationToken)
    {
        if (!_previews.TryRemove(previewToken, out var pending) || pending.InstallationId != installationId
            || pending.ExpiresAt < DateTimeOffset.UtcNow)
            throw new InvalidOperationException("The configuration preview expired. Try the change again.");
        var session = installations.GetRequired(installationId);
        var result = await editor.SaveAsync(session.Installation, pending.Preview, pending.Edits,
            session.Installation.ConfigurationBackupPath, cancellationToken);
        await backups.TrimAsync(session.Installation, pending.Preview.Kind, 20, cancellationToken);
        var listed = await backups.ListAsync(installationId, session.Installation, pending.Preview.Kind, ids, cancellationToken);
        var backupId = listed.FirstOrDefault(item => item.ContentHash.Equals(pending.Preview.BaselineHash,
            StringComparison.OrdinalIgnoreCase))?.Id ?? string.Empty;
        return new(pending.Preview.Kind, backupId, result.ContentHash, result.SavedAt);
    }

    public Task<IReadOnlyList<BackupInfo>> ListBackupsAsync(string installationId, GameConfigurationFileKind kind,
        CancellationToken cancellationToken)
    {
        var session = installations.GetRequired(installationId);
        return backups.ListAsync(installationId, session.Installation, kind, ids, cancellationToken);
    }

    public async Task<RestoreResult> RestoreAsync(string installationId, string backupId,
        CancellationToken cancellationToken)
    {
        var values = ids.Read(backupId, "backup");
        if (values.Count != 3 || values[0] != installationId || !int.TryParse(values[1], out var kindValue)
            || !Enum.IsDefined(typeof(GameConfigurationFileKind), kindValue))
            throw new KeyNotFoundException("The selected backup is unavailable.");
        var session = installations.GetRequired(installationId);
        return await backups.RestoreAsync(session.Installation, values[2], (GameConfigurationFileKind)kindValue,
            cancellationToken);
    }

    public async Task<ModToggleInfo> ToggleModAsync(string installationId, string modId, bool enable,
        CancellationToken cancellationToken)
    {
        var values = ids.Read(modId, "mod");
        if (values.Count != 3 || values[0] != installationId || !int.TryParse(values[1], out var kindValue))
            throw new KeyNotFoundException("The selected Mod is unavailable.");
        var session = installations.GetRequired(installationId);
        var snapshot = await inspector.InspectAsync(session.Installation, cancellationToken);
        var mod = snapshot.Mods.FirstOrDefault(item => (int)item.Kind == kindValue &&
            Path.GetRelativePath(session.Installation.RootPath, item.Path).Equals(values[2], StringComparison.OrdinalIgnoreCase))
            ?? throw new KeyNotFoundException("The selected Mod is unavailable.");
        if (mod.IsEnabled == enable) return new(modId, enable, mod.ContentHash, DateTimeOffset.Now);
        var preview = await mods.PreviewToggleAsync(session.Installation, mod, cancellationToken);
        if (!preview.CanApply) throw new InvalidOperationException(string.Join("; ", preview.Errors));
        var result = await mods.ApplyToggleAsync(session.Installation, preview,
            Path.Combine(session.Installation.LogPath, "mods"), cancellationToken);
        return new(modId, result.IsEnabled, result.ContentHash, result.ChangedAt);
    }

    private void CleanupExpired()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var item in _previews.Where(item => item.Value.ExpiresAt < now))
            _previews.TryRemove(item.Key, out _);
    }

    private sealed record PendingPreview(string InstallationId, ConfigurationChangePreview Preview,
        IReadOnlyList<ConfigurationEdit> Edits, DateTimeOffset ExpiresAt);
}
