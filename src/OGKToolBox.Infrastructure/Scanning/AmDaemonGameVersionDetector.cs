using System.Diagnostics;
using System.Runtime.InteropServices;
using OGKToolBox.Core.Abstractions;
using OGKToolBox.Core.Models;

namespace OGKToolBox.Infrastructure.Scanning;

public sealed class AmDaemonGameVersionDetector : IGameVersionDetector
{
    private static readonly TimeSpan ReadyTimeout = TimeSpan.FromSeconds(30);

    public async Task<GameVersionInfo?> DetectAsync(
        GameInstallation installation,
        IReadOnlyList<DataPackage> packages,
        CancellationToken cancellationToken)
    {
        var root = installation.RootPath;
        if (!OperatingSystem.IsWindows() || IsGameRunning(root)) return null;

        var injectPath = Path.Combine(root, "inject.exe");
        var daemonPath = Path.Combine(root, "amdaemon.exe");
        var hookPath = Path.Combine(root, "mu3hook.dll");
        var apiPath = Path.Combine(root, "mu3_Data", "Plugins", "amdaemon_api.dll");
        var configPaths = new[] { "config_common.json", "config_server.json", "config_client.json" }
            .Select(file => Path.Combine(root, file))
            .ToArray();
        if (!File.Exists(injectPath) || !File.Exists(daemonPath) || !File.Exists(hookPath)
            || !File.Exists(apiPath) || configPaths.Any(path => !File.Exists(path))) return null;

        var startedAt = DateTimeOffset.UtcNow;
        IntPtr nativeLibrary = IntPtr.Zero;
        Process? launcher = null;
        try
        {
            launcher = StartDaemon(injectPath, root);
            nativeLibrary = NativeLibrary.Load(apiPath);
            var coreExecute = GetExport<CoreExecute>(nativeLibrary, "Core_execute");
            var coreIsReady = GetExport<CoreIsReady>(nativeLibrary, "Core_isReady");
            var currentVersion = GetExport<GetCurrentVersion>(nativeLibrary, "AppImage_getCurrentVersion");
            var major = GetExport<GetVersionPart>(nativeLibrary, "Version_major");
            var minor = GetExport<GetVersionPart>(nativeLibrary, "Version_minor");
            var patch = GetExport<GetVersionPart>(nativeLibrary, "Version_patch");
            var deadline = DateTimeOffset.UtcNow + ReadyTimeout;

            while (DateTimeOffset.UtcNow < deadline)
            {
                cancellationToken.ThrowIfCancellationRequested();
                coreExecute();
                if (coreIsReady())
                {
                    var encodedVersion = currentVersion();
                    var appImageVersion = new Version(
                        checked((int)major(encodedVersion)),
                        checked((int)minor(encodedVersion)),
                        checked((int)patch(encodedVersion)));
                    var fallback = GameVersionInfo.FromDataPackages(packages);
                    return new(appImageVersion, fallback.OptionRelease, true);
                }

                await Task.Delay(50, cancellationToken);
            }
        }
        catch (OperationCanceledException) { throw; }
        catch
        {
            // Version detection is advisory. Resource indexing must retain its XML fallback when AM Daemon is unavailable.
        }
        finally
        {
            if (nativeLibrary != IntPtr.Zero) NativeLibrary.Free(nativeLibrary);
            StopDaemonStartedForDetection(root, startedAt);
            if (launcher is not null)
            {
                try
                {
                    if (!launcher.HasExited) launcher.Kill(entireProcessTree: true);
                    launcher.WaitForExit(3000);
                }
                catch
                {
                    // The injector commonly exits once it has launched AM Daemon.
                }
                finally { launcher.Dispose(); }
            }
        }

        return null;
    }

    public Task ReleaseAsync(GameInstallation installation, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsWindows()) return Task.CompletedTask;
        StopProcessAt(installation.RootPath, "inject");
        StopProcessAt(installation.RootPath, "amdaemon");
        return Task.CompletedTask;
    }

    private static bool IsGameRunning(string root) =>
        IsProcessRunningAt("mu3", root) || IsProcessRunningAt("amdaemon", root);

    private static bool IsProcessRunningAt(string name, string root)
    {
        var expectedPath = Path.GetFullPath(Path.Combine(root, name + ".exe"));
        var processes = Process.GetProcessesByName(name);
        try
        {
            foreach (var process in processes)
            {
                try
                {
                    var processPath = process.MainModule?.FileName;
                    if (processPath is not null && Path.GetFullPath(processPath)
                        .Equals(expectedPath, StringComparison.OrdinalIgnoreCase)) return true;
                }
                catch
                {
                    // Some processes deny module inspection; they cannot be attributed to this installation.
                }
            }
            return false;
        }
        finally
        {
            foreach (var process in processes) process.Dispose();
        }
    }

    private static Process StartDaemon(string injectPath, string workingDirectory)
    {
        var startInfo = new ProcessStartInfo(injectPath)
        {
            WorkingDirectory = workingDirectory,
            CreateNoWindow = true,
            UseShellExecute = false,
            WindowStyle = ProcessWindowStyle.Hidden
        };
        foreach (var argument in new[]
        {
            "-d", "-k", "mu3hook.dll", "amdaemon.exe", "-f", "-c",
            "config_common.json", "config_server.json", "config_client.json"
        }) startInfo.ArgumentList.Add(argument);
        return Process.Start(startInfo) ?? throw new InvalidOperationException("无法启动 AM Daemon。");
    }

    private static void StopDaemonStartedForDetection(string root, DateTimeOffset startedAt)
        => StopProcessAt(root, "amdaemon", startedAt);

    private static void StopProcessAt(string root, string name, DateTimeOffset? startedAt = null)
    {
        var expectedPath = Path.GetFullPath(Path.Combine(root, name + ".exe"));
        foreach (var process in Process.GetProcessesByName(name))
        {
            try
            {
                if (startedAt is not null && process.StartTime.ToUniversalTime() < startedAt.Value.UtcDateTime.AddSeconds(-2)
                    || !Path.GetFullPath(process.MainModule?.FileName ?? string.Empty)
                        .Equals(expectedPath, StringComparison.OrdinalIgnoreCase)) continue;
                process.Kill(entireProcessTree: true);
                process.WaitForExit(3000);
            }
            catch
            {
                // The daemon may have exited normally before cleanup.
            }
            finally
            {
                process.Dispose();
            }
        }
    }

    private static T GetExport<T>(IntPtr nativeLibrary, string name) where T : Delegate =>
        Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(nativeLibrary, name));

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate void CoreExecute();

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private delegate bool CoreIsReady();

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate uint GetCurrentVersion();

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate uint GetVersionPart(uint version);
}
