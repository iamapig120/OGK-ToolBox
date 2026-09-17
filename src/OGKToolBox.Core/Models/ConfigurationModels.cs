namespace OGKToolBox.Core.Models;

public enum GameConfigurationFileKind
{
    SegaTools,
    Mu3,
    BepInEx,
    ConfigClient,
    ConfigCommon,
    ConfigServer
}

public enum ConfigurationValueKind
{
    Text,
    Boolean,
    Integer,
    Path,
    Identifier
}

public enum InstalledModKind
{
    MonoModPatch,
    BepInExPlugin
}

public sealed record ConfigurationEntry(
    string Section,
    string Key,
    string Value,
    string DisplayValue,
    ConfigurationValueKind ValueKind,
    int LineNumber,
    string Description,
    bool IsSensitive = false,
    bool IsKnown = false,
    string Locator = "",
    bool IsPresent = true,
    string DefaultValue = "",
    IReadOnlyList<string>? RequiredMods = null)
{
    public IReadOnlyList<string> RequiredModsOrEmpty => RequiredMods ?? [];
}

public sealed record ConfigurationFileSnapshot(
    GameConfigurationFileKind Kind,
    string DisplayName,
    string Path,
    bool Exists,
    string EncodingName,
    string NewLineName,
    DateTimeOffset? LastWriteTime,
    long Size,
    IReadOnlyList<ConfigurationEntry> Entries,
    string ContentHash = "")
{
    public string StatusText => Exists ? $"{Entries.Count} 个配置项" : "未找到";
    public string LastWriteText => LastWriteTime?.ToLocalTime().ToString("yyyy-MM-dd HH:mm") ?? "—";
}

public sealed record InstalledMod(
    string Name,
    string FileName,
    string Path,
    InstalledModKind Kind,
    bool IsEnabled,
    string Version,
    DateTimeOffset LastWriteTime,
    long Size,
    string ContentHash = "",
    string ChineseName = "",
    string Description = "")
{
    public string NameText => string.IsNullOrWhiteSpace(ChineseName) ? Name : ChineseName;
    public string KindText => Kind == InstalledModKind.MonoModPatch ? "MonoMod 补丁" : "BepInEx 插件";
    public string StatusText => IsEnabled ? "已启用" : "已停用";
    public string LastWriteText => LastWriteTime.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
}

public sealed record ModTogglePreview(
    string ModName,
    InstalledModKind Kind,
    bool Enable,
    string SourcePath,
    string TargetPath,
    string SourceHash,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<string> Errors)
{
    public bool CanApply => Errors.Count == 0;
    public string ActionText => Enable ? "启用" : "停用";
}

public sealed record ModToggleResult(
    string ModName,
    bool IsEnabled,
    string Path,
    string ContentHash,
    DateTimeOffset ChangedAt);

public sealed record GameConfigurationSnapshot(
    string GameRoot,
    string HookVersion,
    IReadOnlyList<ConfigurationFileSnapshot> Files,
    IReadOnlyList<InstalledMod> Mods,
    IReadOnlyList<LibraryDiagnostic> Diagnostics);

public sealed record ModConfigurationGroup(
    InstalledMod? Mod,
    string DisplayName,
    string Description,
    IReadOnlyList<ConfigurationEntry> Entries)
{
    public bool IsInstalledMod => Mod is not null;
    public bool IsEnabled => Mod?.IsEnabled ?? true;
    public string Version => Mod?.Version ?? "mu3.ini";
    public bool HasEntries => Entries.Count > 0;
    public bool HasNoEntries => Entries.Count == 0;
}

public sealed record ConfigurationEdit(
    int LineNumber,
    string Locator,
    string OriginalValue,
    string NewValue,
    string Section = "",
    string Key = "",
    bool Remove = false);

public sealed record ConfigurationValueChange(
    string Section,
    string Key,
    string OriginalValue,
    string NewValue,
    int LineNumber);

public sealed record ConfigurationChangePreview(
    GameConfigurationFileKind Kind,
    string Path,
    string BaselineHash,
    string ProposedHash,
    IReadOnlyList<ConfigurationValueChange> Changes,
    IReadOnlyList<string> ValidationErrors)
{
    public bool CanSave => Changes.Count > 0 && ValidationErrors.Count == 0;
}

public sealed record ConfigurationSaveResult(
    string Path,
    string BackupPath,
    string ContentHash,
    DateTimeOffset SavedAt);
