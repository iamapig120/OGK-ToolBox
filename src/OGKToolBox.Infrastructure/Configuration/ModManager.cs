using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OGKToolBox.Core.Abstractions;
using OGKToolBox.Core.Models;

namespace OGKToolBox.Infrastructure.Configuration;

public sealed class ModManager : IModManager
{
    private readonly Func<string?> _findRunningProcess;

    public ModManager() : this(FindRunningProcess) { }
    public ModManager(Func<string?> findRunningProcess) => _findRunningProcess = findRunningProcess;

    public async Task<ModTogglePreview> PreviewToggleAsync(
        GameInstallation installation,
        InstalledMod mod,
        CancellationToken cancellationToken)
    {
        var errors = new List<string>();
        var warnings = new List<string>();
        var source = Path.GetFullPath(mod.Path);
        var allowedRoot = AllowedRoot(installation, mod.Kind);
        if (!IsInside(source, allowedRoot)) errors.Add("Mod 文件不在允许管理的 BepInEx 目录中。");
        if (!File.Exists(source)) errors.Add("Mod 文件已不存在，请重新读取列表。");

        var enable = !mod.IsEnabled;
        var target = TryGetTargetPath(source, mod.Kind, enable, out var suffixError);
        if (suffixError is not null) errors.Add(suffixError);
        if (target is not null && File.Exists(target)) errors.Add($"目标文件已存在：{Path.GetFileName(target)}");

        var hash = string.Empty;
        if (File.Exists(source))
        {
            hash = Hash(await File.ReadAllBytesAsync(source, cancellationToken));
            if (!string.IsNullOrEmpty(mod.ContentHash)
                && !hash.Equals(mod.ContentHash, StringComparison.OrdinalIgnoreCase))
                errors.Add("Mod 文件已被其他程序修改，请重新读取列表。");
        }
        warnings.Add(mod.Kind == InstalledModKind.MonoModPatch
            ? "MonoMod 与目标 Assembly-CSharp 强版本耦合；切换后必须完整重启游戏。"
            : "BepInEx 插件可能带有额外依赖；切换后必须完整重启游戏。");
        warnings.Add("此操作只重命名文件，不验证 Mod 之间的逻辑冲突。 ");
        return new(mod.Name, mod.Kind, enable, source, target ?? string.Empty, hash, warnings, errors);
    }

    public async Task<ModToggleResult> ApplyToggleAsync(
        GameInstallation installation,
        ModTogglePreview confirmedPreview,
        string operationRoot,
        CancellationToken cancellationToken)
    {
        if (!confirmedPreview.CanApply) throw new InvalidOperationException("切换预览包含错误，不能执行。");
        var runningProcess = _findRunningProcess();
        if (runningProcess is not null)
            throw new InvalidOperationException($"检测到 {runningProcess}.exe 正在运行。请完全关闭游戏环境后再切换 Mod。");

        var source = Path.GetFullPath(confirmedPreview.SourcePath);
        var target = Path.GetFullPath(confirmedPreview.TargetPath);
        var allowedRoot = AllowedRoot(installation, confirmedPreview.Kind);
        if (!IsInside(source, allowedRoot) || !IsInside(target, allowedRoot))
            throw new UnauthorizedAccessException("Mod 切换路径超出允许的 BepInEx 目录。");
        var expectedTarget = TryGetTargetPath(source, confirmedPreview.Kind, confirmedPreview.Enable, out var suffixError);
        if (suffixError is not null || !string.Equals(expectedTarget, target, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(suffixError ?? "切换目标文件名不符合规则。");
        if (!File.Exists(source)) throw new FileNotFoundException("Mod 文件已不存在。", source);
        if (File.Exists(target)) throw new IOException($"目标文件已存在：{target}");
        var currentHash = Hash(await File.ReadAllBytesAsync(source, cancellationToken));
        if (!currentHash.Equals(confirmedPreview.SourceHash, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Mod 文件在预览后发生了变化，请重新生成切换预览。");

        Directory.CreateDirectory(operationRoot);
        File.Move(source, target);
        try
        {
            var targetHash = Hash(await File.ReadAllBytesAsync(target, cancellationToken));
            if (!targetHash.Equals(currentHash, StringComparison.Ordinal))
                throw new IOException("重命名后文件哈希不一致。");
            var changedAt = DateTimeOffset.Now;
            await AppendOperationAsync(operationRoot, confirmedPreview, targetHash, changedAt, cancellationToken);
            return new(confirmedPreview.ModName, confirmedPreview.Enable, target, targetHash, changedAt);
        }
        catch
        {
            if (File.Exists(target) && !File.Exists(source))
            {
                try { File.Move(target, source); } catch { }
            }
            throw;
        }
    }

    private static string AllowedRoot(GameInstallation installation, InstalledModKind kind) => Path.GetFullPath(
        kind == InstalledModKind.MonoModPatch
            ? Path.Combine(installation.RootPath, "BepInEx", "monomod")
            : Path.Combine(installation.RootPath, "BepInEx", "plugins"));

    private static bool IsInside(string path, string root)
    {
        var relative = Path.GetRelativePath(root, path);
        return relative.Length > 0 && relative != "."
            && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            && !Path.IsPathRooted(relative);
    }

    private static string? TryGetTargetPath(
        string source,
        InstalledModKind kind,
        bool enable,
        out string? error)
    {
        var enabledSuffix = kind == InstalledModKind.MonoModPatch ? ".mm.dll" : ".dll";
        var disabledSuffix = enabledSuffix + ".disabled";
        error = null;
        if (enable)
        {
            if (!source.EndsWith(disabledSuffix, StringComparison.OrdinalIgnoreCase))
            {
                error = $"停用文件必须以 {disabledSuffix} 结尾。";
                return null;
            }
            return source[..^".disabled".Length];
        }
        if (!source.EndsWith(enabledSuffix, StringComparison.OrdinalIgnoreCase))
        {
            error = $"启用文件必须以 {enabledSuffix} 结尾。";
            return null;
        }
        return source + ".disabled";
    }

    private static string? FindRunningProcess()
    {
        foreach (var processName in new[] { "mu3", "amdaemon", "inject" })
            if (Process.GetProcessesByName(processName).Length > 0)
                return processName;
        return null;
    }

    private static async Task AppendOperationAsync(
        string operationRoot,
        ModTogglePreview preview,
        string contentHash,
        DateTimeOffset changedAt,
        CancellationToken cancellationToken)
    {
        var record = JsonSerializer.Serialize(new
        {
            changedAt,
            preview.ModName,
            kind = preview.Kind.ToString(),
            action = preview.Enable ? "enable" : "disable",
            preview.SourcePath,
            preview.TargetPath,
            contentHash
        });
        await File.AppendAllTextAsync(Path.Combine(operationRoot, "operations.jsonl"),
            record + Environment.NewLine, Encoding.UTF8, cancellationToken);
    }

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
}
