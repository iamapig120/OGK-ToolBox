using System.Collections.Concurrent;
using OGKToolBox.Application.Abstractions;
using OGKToolBox.Application.Contracts;
using OGKToolBox.Core.Abstractions;

namespace OGKToolBox.Application.Services;

public sealed class InstallationRegistry(IGameInstallationValidator validator, ILocalFileSystem files) : IInstallationRegistry
{
    private readonly ConcurrentDictionary<string, InstallationSession> _byId = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, InstallationSession> _byRoot = new(StringComparer.OrdinalIgnoreCase);

    public InstallationSession Register(string rootPath)
    {
        if (!validator.TryValidate(rootPath, out var installation, out var error) || installation is null)
            throw new ArgumentException(error ?? "The selected game installation is invalid.", nameof(rootPath));
        var normalized = Path.GetFullPath(installation.RootPath).TrimEnd(Path.DirectorySeparatorChar);
        if (_byRoot.TryGetValue(normalized, out var existing)) return existing;

        files.CreateDirectory(installation.ToolDataPath);
        files.CreateDirectory(Path.GetDirectoryName(installation.IndexPath)!);
        files.CreateDirectory(installation.AudioCachePath);
        files.CreateDirectory(installation.ConfigurationBackupPath);
        files.CreateDirectory(installation.LogPath);
        files.CreateDirectory(installation.TemporaryPath);

        var displayName = Path.GetFileName(normalized);
        var session = new InstallationSession(Guid.NewGuid().ToString("N"), displayName, installation);
        _byRoot[normalized] = session;
        _byId[session.Id] = session;
        return session;
    }

    public InstallationSession GetRequired(string installationId) =>
        _byId.TryGetValue(installationId, out var session)
            ? session
            : throw new KeyNotFoundException("The installation session is unavailable. Select the game directory again.");

    public bool TryGet(string installationId, out InstallationSession? session) => _byId.TryGetValue(installationId, out session);
}
