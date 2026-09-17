using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OGKToolBox.App.Services;
using OGKToolBox.Core.Abstractions;
using OGKToolBox.Core.Models;

namespace OGKToolBox.App.ViewModels;

public partial class ConfigurationManagementViewModel(
    IGameInstallationValidator validator,
    IGameConfigurationInspector inspector,
    IGameConfigurationEditor editor,
    IModManager modManager,
    AppPaths paths) : ObservableObject
{
    private CancellationTokenSource? _loadCancellation;
    private string _gameRoot = string.Empty;

    public ObservableCollection<ConfigurationFileSnapshot> Files { get; } = [];
    public ObservableCollection<InstalledMod> Mods { get; } = [];
    public ObservableCollection<InstalledMod> FilteredMods { get; } = [];
    public ObservableCollection<ModConfigurationGroup> ModConfigurationGroups { get; } = [];
    public ObservableCollection<LibraryDiagnostic> Diagnostics { get; } = [];

    [ObservableProperty] private ConfigurationFileSnapshot? selectedFile;
    [ObservableProperty] private ConfigurationEntry? selectedEntry;
    [ObservableProperty] private string hookVersion = "未检测";
    [ObservableProperty] private string statusText = "选择游戏目录后读取配置与 Mod。";
    [ObservableProperty] private bool isLoading;
    [ObservableProperty] private string draftValue = string.Empty;
    [ObservableProperty] private ConfigurationChangePreview? changePreview;
    [ObservableProperty] private string lastBackupPath = string.Empty;

    public int ModCount => Mods.Count;
    public int EnabledModCount => Mods.Count(mod => mod.IsEnabled);
    public int UnknownFieldCount => Files.Sum(file => file.Entries.Count(entry => !entry.IsKnown));
    public string ModSummary => $"{ModCount} 个 Mod · {EnabledModCount} 个已启用";
    public string ConfigurationSummary => $"{Files.Count(file => file.Exists)} / {Files.Count} 个配置文件 · {UnknownFieldCount} 个未收录字段";
    public bool CanEditSelectedEntry => SelectedFile is not null && SelectedEntry is not null
        && SelectedFile.Exists && !SelectedEntry.IsSensitive
        && SelectedFile.Kind is GameConfigurationFileKind.SegaTools or GameConfigurationFileKind.Mu3 or GameConfigurationFileKind.BepInEx;
    public bool HasDraftChange => CanEditSelectedEntry
        && !string.Equals(DraftValue, SelectedEntry!.Value, StringComparison.Ordinal);
    public bool HasChangePreview => ChangePreview is not null;
    public bool CanSavePreview => ChangePreview?.CanSave == true;
    public string PreviewSummary => ChangePreview is null ? "修改值后先生成差异预览。" :
        ChangePreview.CanSave
            ? $"将修改 {ChangePreview.Changes.Count} 项；保存时会再次检查文件是否发生变化。"
            : string.Join("；", ChangePreview.ValidationErrors);
    public bool CanToggleMods => !IsLoading;
    partial void OnIsLoadingChanged(bool value) => OnPropertyChanged(nameof(CanToggleMods));
    partial void OnSelectedFileChanged(ConfigurationFileSnapshot? value)
    {
        SelectedEntry = value?.Entries.FirstOrDefault();
        ChangePreview = null;
        NotifyEditState();
    }
    partial void OnSelectedEntryChanged(ConfigurationEntry? value)
    {
        DraftValue = value?.Value ?? string.Empty;
        ChangePreview = null;
        NotifyEditState();
    }
    partial void OnDraftValueChanged(string value)
    {
        ChangePreview = null;
        NotifyEditState();
    }
    partial void OnChangePreviewChanged(ConfigurationChangePreview? value) => NotifyEditState();

    public async Task LoadAsync(string gameRoot)
    {
        _gameRoot = gameRoot;
        _loadCancellation?.Cancel();
        _loadCancellation?.Dispose();
        _loadCancellation = new CancellationTokenSource();
        if (!validator.TryValidate(gameRoot, out var installation, out var error))
        {
            Clear();
            StatusText = error ?? "游戏目录无效。";
            return;
        }

        IsLoading = true;
        StatusText = "正在读取 segatools 与 BepInEx 状态…";
        try
        {
            var snapshot = await inspector.InspectAsync(installation!, _loadCancellation.Token);
            Replace(Files, snapshot.Files);
            Replace(Mods, snapshot.Mods);
            Replace(Diagnostics, snapshot.Diagnostics);
            Replace(ModConfigurationGroups, BuildModConfigurationGroups(snapshot));
            HookVersion = snapshot.HookVersion;
            SelectedFile = Files.FirstOrDefault(file => file.Exists) ?? Files.FirstOrDefault();
            Replace(FilteredMods, Mods);
            NotifySummaries();
            StatusText = $"配置检查完成 · {ConfigurationSummary} · {ModSummary}";
        }
        catch (OperationCanceledException) { }
        catch (Exception exception)
        {
            Clear();
            StatusText = $"配置检查失败：{exception.Message}";
        }
        finally { IsLoading = false; }
    }

    [RelayCommand]
    private Task RefreshAsync() => LoadAsync(_gameRoot);

    [RelayCommand]
    private async Task PreviewChangeAsync()
    {
        if (!CanEditSelectedEntry || SelectedFile is null || SelectedEntry is null) return;
        var edit = CurrentEdit();
        ChangePreview = await editor.PreviewAsync(new GameInstallation(_gameRoot), SelectedFile.Kind,
            [edit], SelectedFile.ContentHash, CancellationToken.None);
        OnPropertyChanged(nameof(PreviewSummary));
        StatusText = ChangePreview.CanSave ? "差异预览已生成；确认无误后可以保存。" : PreviewSummary;
    }

    [RelayCommand]
    private async Task SavePreviewAsync()
    {
        if (!CanSavePreview || ChangePreview is null || SelectedFile is null || SelectedEntry is null) return;
        IsLoading = true;
        try
        {
            var kind = SelectedFile.Kind;
            var key = SelectedEntry.Key;
            var result = await editor.SaveAsync(new GameInstallation(_gameRoot), ChangePreview,
                [CurrentEdit()], paths.ConfigurationBackupPath, CancellationToken.None);
            LastBackupPath = result.BackupPath;
            await LoadAsync(_gameRoot);
            SelectedFile = Files.FirstOrDefault(file => file.Kind == kind);
            SelectedEntry = SelectedFile?.Entries.FirstOrDefault(entry => entry.Key == key);
            StatusText = $"已安全保存 {Path.GetFileName(result.Path)}；原文件已备份。";
        }
        catch (Exception exception) { StatusText = $"保存失败：{exception.Message}"; }
        finally { IsLoading = false; }
    }

    [RelayCommand]
    private void RevertDraft()
    {
        DraftValue = SelectedEntry?.Value ?? string.Empty;
        ChangePreview = null;
        StatusText = "已放弃尚未保存的草稿。";
    }

    [RelayCommand]
    private void SetDraftValue(string? value)
    {
        if (CanEditSelectedEntry) DraftValue = value ?? string.Empty;
    }

    [RelayCommand]
    private async Task ToggleModAsync(InstalledMod? mod)
    {
        if (mod is null || IsLoading) return;
        if (!validator.TryValidate(_gameRoot, out var installation, out var error))
        {
            StatusText = error ?? "游戏目录无效。";
            return;
        }
        IsLoading = true;
        try
        {
            var preview = await modManager.PreviewToggleAsync(installation!, mod, CancellationToken.None);
            if (!preview.CanApply)
            {
                StatusText = $"无法{preview.ActionText}“{mod.NameText}”：{string.Join("；", preview.Errors)}";
                return;
            }

            var result = await modManager.ApplyToggleAsync(installation!, preview,
                paths.ModOperationPath, CancellationToken.None);
            await LoadAsync(_gameRoot);
            StatusText = $"已{(result.IsEnabled ? "启用" : "停用")}“{mod.NameText}”；重启游戏后生效。";
        }
        catch (Exception exception) { StatusText = $"Mod 切换失败：{exception.Message}"; }
        finally { IsLoading = false; }
    }

    private void Clear()
    {
        Files.Clear();
        Mods.Clear();
        FilteredMods.Clear();
        ModConfigurationGroups.Clear();
        Diagnostics.Clear();
        SelectedFile = null;
        SelectedEntry = null;
        HookVersion = "未检测";
        NotifySummaries();
    }

    private void NotifySummaries()
    {
        OnPropertyChanged(nameof(ModCount));
        OnPropertyChanged(nameof(EnabledModCount));
        OnPropertyChanged(nameof(ModSummary));
        OnPropertyChanged(nameof(UnknownFieldCount));
        OnPropertyChanged(nameof(ConfigurationSummary));
    }

    private ConfigurationEdit CurrentEdit() => new(SelectedEntry!.LineNumber, SelectedEntry.Locator,
        SelectedEntry.Value, DraftValue, SelectedEntry.Section, SelectedEntry.Key);

    private void NotifyEditState()
    {
        OnPropertyChanged(nameof(CanEditSelectedEntry));
        OnPropertyChanged(nameof(HasDraftChange));
        OnPropertyChanged(nameof(HasChangePreview));
        OnPropertyChanged(nameof(CanSavePreview));
        OnPropertyChanged(nameof(PreviewSummary));
    }

    private static void Replace<T>(ObservableCollection<T> collection, IEnumerable<T> items)
    {
        collection.Clear();
        foreach (var item in items) collection.Add(item);
    }

    private static IEnumerable<ModConfigurationGroup> BuildModConfigurationGroups(GameConfigurationSnapshot snapshot)
    {
        var mu3 = snapshot.Files.FirstOrDefault(file => file.Kind == GameConfigurationFileKind.Mu3);
        if (mu3 is null || !mu3.Exists) return [];

        var remaining = mu3.Entries.ToList();
        var groups = new List<ModConfigurationGroup>();
        foreach (var mod in snapshot.Mods)
        {
            var entries = mod.IsEnabled
                ? remaining.Where(entry => entry.RequiredModsOrEmpty.Contains(mod.Name,
                    StringComparer.OrdinalIgnoreCase)).ToArray()
                : [];
            groups.Add(new(mod, mod.NameText, mod.Description, entries));
            remaining.RemoveAll(entry => entries.Contains(entry));
        }

        if (remaining.Count > 0)
            groups.Add(new(null, "原版与独立 mu3.ini 配置", "不依赖已启用 Mod 的原版或现有配置项。", remaining));
        return groups;
    }
}
