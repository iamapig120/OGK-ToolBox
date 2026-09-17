using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using OGKToolBox.App.Services;
using OGKToolBox.Core.Abstractions;
using OGKToolBox.Core.Models;

namespace OGKToolBox.App.ViewModels;

public sealed record NavigationItem(string Name, string Glyph);
public sealed record UiScaleOption(string Label, double Scale);
public enum CardDisplayMode { FinalCard, FullIllustration, TransparentCharacter }
public sealed record VisualOption(string Label, ResourceReference Resource, CardDisplayMode Mode);
public sealed record CharacterExpression(string Label, string BundlePath, UnitySpriteInfo Sprite)
{
    public ImageSource? CachedImage { get; set; }
}

public partial class SelectableFilterOption(string value, string label) : ObservableObject
{
    public string Value { get; } = value;
    public string Label { get; } = label;
    [ObservableProperty] private bool isSelected;
}

public sealed class BulkObservableCollection<T> : ObservableCollection<T>
{
    public void ReplaceAll(IEnumerable<T> source)
    {
        Items.Clear();
        foreach (var item in source) Items.Add(item);
        OnPropertyChanged(new(nameof(Count)));
        OnPropertyChanged(new("Item[]"));
        OnCollectionChanged(new(NotifyCollectionChangedAction.Reset));
    }
}

public partial class MainViewModel(
    IGameInstallationValidator validator,
    ILibraryScanner scanner,
    IDataPackageResolver packageResolver,
    IGameVersionDetector gameVersionDetector,
    IUnityResourceReader unityReader,
    IAudioPreviewService audio,
    IChartPreviewBuilder chartPreviewBuilder,
    IResourceExporter exporter,
    ChartSoundEffectService chartSounds,
    SettingsService settings,
    ThemeService themes,
    ConfigurationManagementViewModel configuration,
    AppPaths paths) : ObservableObject
{
    private CancellationTokenSource? _scanCancellation;
    private CancellationTokenSource? _gameVersionCancellation;
    private CancellationTokenSource? _expressionLoadCancellation;
    private CancellationTokenSource? _previewCancellation;
    private CancellationTokenSource? _bundleLoadCancellation;
    private CancellationTokenSource? _characterListCancellation;
    private CancellationTokenSource? _chartPreviewCancellation;
    private int _expressionLoadVersion;
    private int _gameVersionRequest;
    private int _previewVersion;
    private int _bundleLoadVersion;
    private bool _isUpdatingCardFilters;
    private bool _cardsLoaded;
    private bool _chartTimerInitialized;
    private bool _isAdvancingChart;
    private bool _chartAudioReady;
    private int _nextChartSoundEvent;
    private long _lastChartFrame;
    private readonly List<ChartTimedSoundEvent> _chartSoundEvents = [];
    private readonly MediaPlayer _player = new();
    private readonly DispatcherTimer _chartTimer = new(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(16) };
    private readonly DispatcherTimer _environmentTimer = new(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(2) };
    private LibrarySnapshot? _snapshot;
    private GameVersionInfo? _gameVersion;
    private Character? _loadedExpressionCharacter;

    public BulkObservableCollection<Music> Music { get; } = [];
    public BulkObservableCollection<Card> Cards { get; } = [];
    public BulkObservableCollection<Card> FilteredCards { get; } = [];
    public BulkObservableCollection<SelectableFilterOption> CardCharacterFilters { get; } = [];
    public BulkObservableCollection<SelectableFilterOption> CardRarityFilters { get; } = [];
    public BulkObservableCollection<SelectableFilterOption> CardSourceFilters { get; } = [];
    public BulkObservableCollection<Character> Characters { get; } = [];
    public BulkObservableCollection<GameResource> Resources { get; } = [];
    public BulkObservableCollection<LibraryDiagnostic> Diagnostics { get; } = [];
    public ObservableCollection<UnityImageInfo> BundleImages { get; } = [];
    public ObservableCollection<VisualOption> CardVisuals { get; } = [];
    public ObservableCollection<CharacterExpression> CharacterExpressions { get; } = [];
    public ConfigurationManagementViewModel Configuration { get; } = configuration;
    public IReadOnlyList<double> ChartPlaybackSpeeds { get; } = [.5, .75, 1, 1.25, 1.5, 2];
    public IReadOnlyList<double> ChartScrollSpeeds { get; } = Enumerable.Range(0, 77)
        .Select(index => 1d + index * .25d).ToArray();
    public IReadOnlyList<NavigationItem> NavigationItems { get; } =
    [
        new("首页", "\uE80F"), new("乐曲", "\uE8D6"), new("卡片", "\uE8D4"),
        new("角色", "\uE77B"), new("资源浏览器", "\uE8B7"), new("Segatools", "\uE950"),
        new("Mod 管理", "\uE74C"), new("诊断", "\uE9D9"), new("设置", "\uE713")
    ];

    [ObservableProperty] private string currentPage = "首页";
    [ObservableProperty] private NavigationItem? selectedNavigation;
    [ObservableProperty] private string gameRoot = string.Empty;
    [ObservableProperty] private string searchText = string.Empty;
    [ObservableProperty] private string statusText = "请选择游戏 package 目录，开始建立只读资源索引。";
    [ObservableProperty] private bool isScanning;
    [ObservableProperty] private double scanPercent;
    [ObservableProperty] private Music? selectedMusic;
    [ObservableProperty] private Chart? selectedChart;
    [ObservableProperty] private ChartPreview? chartPreview;
    [ObservableProperty] private bool isLoadingChartPreview;
    [ObservableProperty] private bool isChartPlayerOpen;
    [ObservableProperty] private bool isChartPlaying;
    [ObservableProperty] private double chartPositionSeconds;
    [ObservableProperty] private double chartPlaybackSpeed = 1;
    [ObservableProperty] private double chartScrollSpeed = 6;
    [ObservableProperty] private bool chartUsesGameSounds;
    [ObservableProperty] private Card? selectedCard;
    [ObservableProperty] private VisualOption? selectedCardVisual;
    [ObservableProperty] private CardDisplayMode selectedCardDisplayMode = CardDisplayMode.FinalCard;
    [ObservableProperty] private Character? selectedCharacter;
    [ObservableProperty] private bool isLoadingCharacters;
    [ObservableProperty] private CharacterExpression? selectedCharacterExpression;
    [ObservableProperty] private bool isLoadingCharacterExpressions;
    [ObservableProperty] private GameResource? selectedResource;
    [ObservableProperty] private UnityImageInfo? selectedBundleImage;
    [ObservableProperty] private ImageSource? previewImage;
    [ObservableProperty] private bool isImageViewerOpen;
    [ObservableProperty] private bool isControllerConnected;
    [ObservableProperty] private string controllerStatus = "\u672A\u8FDE\u63A5";
    [ObservableProperty] private AppTheme selectedTheme;
    [ObservableProperty] private double selectedUiScale = 1d;

    public IReadOnlyList<UiScaleOption> UiScaleOptions { get; } =
    [
        new("\u5C0F\u53F7", 1d),
        new("125%", 1.25d),
        new("150%", 1.5d),
        new("175%", 1.75d),
        new("200%", 2d)
    ];

    public int MusicCount => _snapshot?.EffectiveMusic.Count ?? 0;
    public int ChartCount => _snapshot?.EffectiveMusic.Sum(item => item.Charts.Count) ?? 0;
    public int ResourceCount => _snapshot?.Resources.Count ?? 0;
    public int PackageCount => _snapshot?.Packages.Count ?? 0;
    public int ErrorCount => _snapshot?.Diagnostics.Count(item => item.Severity == DiagnosticSeverity.Error) ?? 0;
    public string GameVersionDisplay => _gameVersion?.Display ?? "\u672A\u68C0\u6D4B";
    public bool IsSegaToolsInstalled => Configuration.Files.Any(
        item => item.Kind == GameConfigurationFileKind.SegaTools && item.Exists);
    public string SegaToolsStatus => IsSegaToolsInstalled ? "\u5DF2\u5B89\u88C5" : "\u672A\u5B89\u88C5";
    public string HealthStatus => ErrorCount == 0 ? "\u72B6\u6001\u826F\u597D" : $"{ErrorCount} \u9879\u9700\u8981\u6CE8\u610F";
    public int FilteredCardCount => FilteredCards.Count;
    public string CardCharacterFilterLabel => FilterLabel("角色", CardCharacterFilters);
    public string CardRarityFilterLabel => FilterLabel("稀有度", CardRarityFilters);
    public string CardSourceFilterLabel => FilterLabel("来源", CardSourceFilters);
    public bool HasActiveCardFilters => CardCharacterFilters.Any(item => item.IsSelected)
        || CardRarityFilters.Any(item => item.IsSelected)
        || CardSourceFilters.Any(item => item.IsSelected);
    public bool IsFinalCardMode => SelectedCardDisplayMode == CardDisplayMode.FinalCard;
    public bool IsFullIllustrationMode => SelectedCardDisplayMode == CardDisplayMode.FullIllustration;
    public bool IsTransparentCharacterMode => SelectedCardDisplayMode == CardDisplayMode.TransparentCharacter;
    public string GameRootDisplay => string.IsNullOrWhiteSpace(GameRoot) ? "尚未选择游戏目录" : GameRoot;
    public string ChartPreviewSummary => ChartPreview is null ? "选择一个存在的谱面以生成只读预览。" :
        $"{ChartPreview.MeasureCount:0.#} 小节 · {ChartPreview.Lanes.Count:N0} 条轨道 · {ChartPreview.Notes.Count:N0} 个可视事件" +
        (ChartPreview.UnknownCommandCount > 0 ? $" · {ChartPreview.UnknownCommandCount:N0} 个命令暂未解释" : string.Empty);
    public double ChartDurationSeconds => ChartPreview?.DurationSeconds ?? 0;
    public string ChartPositionText => $"{FormatTime(ChartPositionSeconds)} / {FormatTime(ChartDurationSeconds)}";
    public string ChartPlaybackLabel => IsChartPlaying ? "暂停" : "播放";
    public string ChartPlaybackGlyph => IsChartPlaying ? "\uE769" : "\uE768";
    public string ChartScrollSpeedText => ChartScrollSpeed >= 20 ? "SONIC" : $"{ChartScrollSpeed:0.00}";
    public bool CanDecreaseChartScrollSpeed => ChartScrollSpeed > 1;
    public bool CanIncreaseChartScrollSpeed => ChartScrollSpeed < 20;

    public async Task InitializeAsync()
    {
        if (!_chartTimerInitialized)
        {
            _chartTimer.Tick += AdvanceChartPlayback;
            _chartTimerInitialized = true;
        }
        _environmentTimer.Tick -= RefreshEnvironmentStatus;
        _environmentTimer.Tick += RefreshEnvironmentStatus;
        _environmentTimer.Start();
        RefreshEnvironmentStatus();
        SelectedNavigation ??= NavigationItems[0];
        GameRoot = settings.Current.GameRoot;
        SelectedTheme = settings.Current.Theme;
        SelectedUiScale = settings.Current.UiScale;
        ChartScrollSpeed = Math.Clamp(settings.Current.ChartScrollSpeed, 1, 20);
        if (!string.IsNullOrWhiteSpace(GameRoot) && validator.TryValidate(GameRoot, out _, out _))
        {
            await LoadCachedOrScanAsync();
        }
    }

    partial void OnSearchTextChanged(string value) => RefreshMusic();
    partial void OnSelectedUiScaleChanged(double value)
    {
        if (Math.Abs(settings.Current.UiScale - value) < 0.001) return;
        settings.Current.UiScale = value;
        _ = SaveSettingsQuietlyAsync();
    }
    partial void OnSelectedNavigationChanged(NavigationItem? value) { if (value is not null) CurrentPage = value.Name; }
    partial void OnCurrentPageChanged(string value)
    {
        switch (value)
        {
            case "卡片" when !_cardsLoaded:
                _ = LoadCardsAsync();
                break;
            case "乐曲": _ = LoadPreviewAsync(SelectedMusic?.Jacket); break;
            case "卡片": _ = LoadPreviewAsync(SelectedCardVisual?.Resource); break;
            case "角色" when ReferenceEquals(_loadedExpressionCharacter, SelectedCharacter)
                && SelectedCharacterExpression is not null:
                _ = LoadExpressionPreviewAsync(SelectedCharacterExpression);
                break;
            case "角色": _ = LoadCharacterExpressionsAsync(SelectedCharacter); break;
            case "资源浏览器" when SelectedBundleImage is not null: _ = LoadBundleImageAsync(SelectedBundleImage); break;
            case "资源浏览器": _ = LoadBundleAsync(SelectedResource); break;
            default: ResetPreview(); break;
        }
    }
    partial void OnGameRootChanged(string value) => OnPropertyChanged(nameof(GameRootDisplay));
    partial void OnSelectedMusicChanged(Music? value)
    {
        CloseChartPlayer();
        SelectedChart = value?.Charts.FirstOrDefault(chart => chart.Exists);
        if (CurrentPage == "乐曲") _ = LoadPreviewAsync(value?.Jacket);
    }
    partial void OnSelectedChartChanged(Chart? value)
    {
        if (IsChartPlayerOpen) CloseChartPlayer();
        ChartPreview = null;
    }
    partial void OnChartPreviewChanged(ChartPreview? value)
    {
        RebuildChartSoundEvents(value);
        OnPropertyChanged(nameof(ChartPreviewSummary));
        OnPropertyChanged(nameof(ChartDurationSeconds));
        OnPropertyChanged(nameof(ChartPositionText));
    }
    partial void OnChartPositionSecondsChanged(double value)
    {
        OnPropertyChanged(nameof(ChartPositionText));
        if (!_isAdvancingChart && _chartAudioReady) _player.Position = TimeSpan.FromSeconds(value);
        if (!_isAdvancingChart)
        {
            chartSounds.StopAll();
            ResetChartSoundCursor(value);
            if (IsChartPlaying) RestoreChartLoopsAt(value);
        }
    }
    partial void OnChartPlaybackSpeedChanged(double value)
    {
        _player.SpeedRatio = value;
        _lastChartFrame = Stopwatch.GetTimestamp();
    }
    partial void OnChartScrollSpeedChanged(double value)
    {
        if (value < 1 || value > 20) return;
        OnPropertyChanged(nameof(ChartScrollSpeedText));
        OnPropertyChanged(nameof(CanDecreaseChartScrollSpeed));
        OnPropertyChanged(nameof(CanIncreaseChartScrollSpeed));
        settings.Current.ChartScrollSpeed = value;
        _ = SaveSettingsQuietlyAsync();
    }
    partial void OnIsChartPlayingChanged(bool value)
    {
        OnPropertyChanged(nameof(ChartPlaybackLabel));
        OnPropertyChanged(nameof(ChartPlaybackGlyph));
    }
    partial void OnSelectedCardChanged(Card? value) => PopulateCardVisuals(value);
    partial void OnSelectedCardDisplayModeChanged(CardDisplayMode value)
    {
        OnPropertyChanged(nameof(IsFinalCardMode));
        OnPropertyChanged(nameof(IsFullIllustrationMode));
        OnPropertyChanged(nameof(IsTransparentCharacterMode));
        SelectCardVisualForCurrentMode();
    }
    partial void OnSelectedCardVisualChanged(VisualOption? value) { if (CurrentPage == "卡片") _ = LoadPreviewAsync(value?.Resource); }
    partial void OnSelectedCharacterChanged(Character? value)
    {
        if (CurrentPage == "角色") _ = LoadCharacterExpressionsAsync(value);
    }
    partial void OnSelectedCharacterExpressionChanged(CharacterExpression? value) { if (CurrentPage == "角色") _ = LoadExpressionPreviewAsync(value); }
    partial void OnSelectedResourceChanged(GameResource? value) { if (CurrentPage == "资源浏览器") _ = LoadBundleAsync(value); }
    partial void OnSelectedBundleImageChanged(UnityImageInfo? value) { if (CurrentPage == "资源浏览器") _ = LoadBundleImageAsync(value); }

    [RelayCommand]
    private void Navigate(string page) => CurrentPage = page;

    [RelayCommand]
    private async Task ChooseGameFolderAsync()
    {
        var dialog = new OpenFolderDialog { Title = "选择 ONGEKI 的 package 目录", Multiselect = false };
        if (Directory.Exists(GameRoot)) dialog.InitialDirectory = GameRoot;
        if (dialog.ShowDialog() != true) return;
        GameRoot = dialog.FolderName;
        await LoadCachedOrScanAsync();
    }

    private async Task LoadCachedOrScanAsync()
    {
        CancelGameVersionDetection();
        await Configuration.LoadAsync(GameRoot);
        NotifyEnvironmentStatus();
        if (!validator.TryValidate(GameRoot, out var installation, out var error))
        {
            StatusText = error ?? "游戏目录无效。";
            return;
        }

        IsScanning = true;
        ScanPercent = 8;
        StatusText = "正在恢复本地资源索引…";
        try
        {
            _snapshot = await Task.Run(() => scanner.LoadCachedAsync(installation!, CancellationToken.None));
            if (_snapshot is not null)
            {
                SetTemporaryGameVersion(_snapshot.Packages);
                await PopulateCollectionsAsync();
                ScanPercent = 100;
                StatusText = $"已从缓存载入 · {_snapshot.EffectiveMusic.Count:N0} 首乐曲 · {_snapshot.Cards.Count:N0} 张卡片";
                // 立即检测版本号
                await DetectAndSetGameVersionAsync(installation!, _snapshot);
                return;
            }
        }
        catch (Exception exception)
        {
            StatusText = $"缓存不可用，将重新扫描：{exception.Message}";
        }
        finally
        {
            IsScanning = false;
        }

        await ScanAsync();
    }

    [RelayCommand]
    private async Task ScanAsync()
    {
        if (!validator.TryValidate(GameRoot, out var installation, out var error))
        {
            StatusText = error ?? "游戏目录无效。";
            return;
        }

        _scanCancellation?.Cancel();
        _scanCancellation = new CancellationTokenSource();
        IsScanning = true;
        ScanPercent = 0;
        StatusText = "正在扫描资源…";
        var temporaryPackages = await Task.Run(
            () => packageResolver.DiscoverAsync(installation!, _scanCancellation.Token),
            _scanCancellation.Token);
        SetTemporaryGameVersion(temporaryPackages);
        var progress = new Progress<ScanProgress>(value =>
        {
            StatusText = value.CurrentPath is null ? value.Phase : $"{value.Phase} · {Path.GetFileName(value.CurrentPath)}";
            ScanPercent = OverallScanPercent(value);
        });
        try
        {
            _snapshot = await Task.Run(
                () => scanner.ScanAsync(installation!, progress, _scanCancellation.Token),
                _scanCancellation.Token);
            settings.Current.GameRoot = GameRoot;
            await settings.SaveAsync();
            await PopulateCollectionsAsync();
            StatusText = $"扫描完成 · {_snapshot.EffectiveMusic.Count:N0} 首乐曲 · {_snapshot.Resources.Count:N0} 个资源";
            // 立即检测版本号
            await DetectAndSetGameVersionAsync(installation!, _snapshot);
        }
        catch (OperationCanceledException) { StatusText = "扫描已取消。"; }
        catch (Exception exception) { StatusText = $"扫描失败：{exception.Message}"; }
        finally { IsScanning = false; }
    }

    [RelayCommand]
    private void CancelScan() => _scanCancellation?.Cancel();

    private void SetTemporaryGameVersion(IReadOnlyList<DataPackage> packages)
    {
        _gameVersion = GameVersionInfo.FromDataPackages(packages);
        OnPropertyChanged(nameof(GameVersionDisplay));
    }
    private async Task DetectAndSetGameVersionAsync(GameInstallation installation, LibrarySnapshot snapshot)
    {
        CancelGameVersionDetection();
        var request = ++_gameVersionRequest;
        _gameVersionCancellation = new CancellationTokenSource();
        
        try
        {
            var detected = await Task.Run(() => 
                gameVersionDetector.DetectAsync(installation, snapshot.Packages, _gameVersionCancellation.Token));
            
            if (detected is null || request != _gameVersionRequest || !ReferenceEquals(_snapshot, snapshot)) 
                return;

            snapshot.GameVersion = detected;
            _gameVersion = detected;
            OnPropertyChanged(nameof(GameVersionDisplay));
            StatusText = $"游戏版本：{detected.Display}";
        }
        catch (Exception) { /* 版本检测失败不影响主流程 */ }
    }

    private void StartBackgroundGameVersionDetection(GameInstallation installation, LibrarySnapshot snapshot)
    {
        CancelGameVersionDetection();
        var request = ++_gameVersionRequest;
        _gameVersionCancellation = new CancellationTokenSource();
        _ = DetectGameVersionAsync(installation, snapshot, request, _gameVersionCancellation.Token);
    }

    private async Task DetectGameVersionAsync(
        GameInstallation installation,
        LibrarySnapshot snapshot,
        int request,
        CancellationToken cancellationToken)
    {
        try
        {
            var detected = await Task.Run(
                () => gameVersionDetector.DetectAsync(installation, snapshot.Packages, cancellationToken),
                cancellationToken);
            if (detected is null || cancellationToken.IsCancellationRequested || request != _gameVersionRequest
                || !ReferenceEquals(_snapshot, snapshot)) return;

            snapshot.GameVersion = detected;
            _gameVersion = detected;
            OnPropertyChanged(nameof(GameVersionDisplay));
        }
        catch (OperationCanceledException) { }
    }

    private void CancelGameVersionDetection()
    {
        _gameVersionRequest++;
        _gameVersionCancellation?.Cancel();
        _gameVersionCancellation?.Dispose();
        _gameVersionCancellation = null;
    }

    [RelayCommand]
    private void OpenImageViewer()
    {
        if (PreviewImage is not null) IsImageViewerOpen = true;
    }

    [RelayCommand]
    private void CloseImageViewer() => IsImageViewerOpen = false;

    [RelayCommand]
    private void CycleImageViewer(int step)
    {
        if (!IsImageViewerOpen || step == 0) return;
        switch (CurrentPage)
        {
            case "角色":
                CycleCharacterExpression(step);
                break;
            case "卡片":
                var visual = Cycle(CardVisuals, SelectedCardVisual, step);
                if (visual is not null) SelectedCardDisplayMode = visual.Mode;
                break;
            case "乐曲":
                var musicWithJackets = Music.Where(item => item.Jacket is not null).ToArray();
                SelectedMusic = Cycle(musicWithJackets, SelectedMusic, step);
                break;
        }
    }

    [RelayCommand]
    private void CycleCharacterExpression(int step)
    {
        if (step != 0)
            SelectedCharacterExpression = Cycle(CharacterExpressions, SelectedCharacterExpression, step);
    }

    private static T? Cycle<T>(IReadOnlyList<T> items, T? current, int step) where T : class
    {
        if (items.Count == 0) return null;
        var currentIndex = -1;
        if (current is not null)
        {
            for (var index = 0; index < items.Count; index++)
            {
                if (!EqualityComparer<T>.Default.Equals(items[index], current)) continue;
                currentIndex = index;
                break;
            }
        }
        var nextIndex = currentIndex < 0 ? 0 : (currentIndex + Math.Sign(step) + items.Count) % items.Count;
        return items[nextIndex];
    }

    [RelayCommand]
    private void SetCardDisplayMode(string mode)
    {
        if (Enum.TryParse<CardDisplayMode>(mode, out var value)) SelectedCardDisplayMode = value;
    }

    [RelayCommand]
    private void ClearCardFilters()
    {
        _isUpdatingCardFilters = true;
        try
        {
            foreach (var option in CardCharacterFilters.Concat(CardRarityFilters).Concat(CardSourceFilters))
                option.IsSelected = false;
        }
        finally
        {
            _isUpdatingCardFilters = false;
        }
        RefreshCards();
    }

    [RelayCommand]
    private async Task SetThemeAsync(string themeName)
    {
        if (!Enum.TryParse<AppTheme>(themeName, out var theme)) return;
        SelectedTheme = theme;
        settings.Current.Theme = theme;
        themes.Apply(theme);
        await settings.SaveAsync();
    }

    [RelayCommand]
    private async Task PlayAudioAsync()
    {
        if (SelectedMusic?.Audio is null) { StatusText = "这首乐曲没有可用音频。"; return; }
        try
        {
            StatusText = "正在准备音频预览…";
            var wave = await audio.DecodeToWaveAsync(SelectedMusic.Audio, paths.AudioCachePath, CancellationToken.None);
            _player.Open(new Uri(wave));
            _player.Play();
            StatusText = $"正在播放 · {SelectedMusic.Title}";
        }
        catch (Exception exception) { StatusText = exception.Message; }
    }

    [RelayCommand]
    private void StopAudio() => _player.Stop();

    [RelayCommand]
    private void SelectChart(Chart chart) => SelectedChart = chart;

    [RelayCommand]
    private async Task OpenChartPlayerAsync()
    {
        if (SelectedChart is null || !SelectedChart.Exists) return;
        StopChartPlayback();
        ChartPositionSeconds = 0;
        IsChartPlayerOpen = true;
        await LoadChartPreviewAsync(SelectedChart);
        if (ChartPreview is null) IsChartPlayerOpen = false;
        else
        {
            _ = PrepareChartGameResourcesAsync();
            if (SelectedMusic is not null) _ = PrepareChartAudioAsync(SelectedMusic);
            ToggleChartPlayback();
        }
    }

    [RelayCommand]
    private void CloseChartPlayer()
    {
        StopChartPlayback();
        IsChartPlayerOpen = false;
        _chartAudioReady = false;
        chartSounds.StopAll();
        ChartUsesGameSounds = false;
        ChartPreview = null;
        ChartPositionSeconds = 0;
    }

    [RelayCommand]
    private void ToggleChartPlayback()
    {
        if (ChartPreview is null) return;
        if (IsChartPlaying) { StopChartPlayback(preservePosition: true); return; }
        if (ChartPositionSeconds >= ChartDurationSeconds - .01) ChartPositionSeconds = 0;
        ResetChartSoundCursor(ChartPositionSeconds);
        RestoreChartLoopsAt(ChartPositionSeconds);
        IsChartPlaying = true;
        _lastChartFrame = Stopwatch.GetTimestamp();
        _chartTimer.Start();
        if (_chartAudioReady)
        {
            _player.Position = TimeSpan.FromSeconds(ChartPositionSeconds);
            _player.SpeedRatio = ChartPlaybackSpeed;
            _player.Play();
        }
    }

    [RelayCommand]
    private void SeekChart(double seconds)
    {
        ChartPositionSeconds = Math.Clamp(ChartPositionSeconds + seconds, 0, ChartDurationSeconds);
        _lastChartFrame = Stopwatch.GetTimestamp();
    }

    [RelayCommand]
    private void SeekChartBackward() => SeekChart(-5);

    [RelayCommand]
    private void SeekChartForward() => SeekChart(5);

    [RelayCommand]
    private void DecreaseChartScrollSpeed() => StepChartScrollSpeed(-1);

    [RelayCommand]
    private void IncreaseChartScrollSpeed() => StepChartScrollSpeed(1);

    private void StepChartScrollSpeed(int direction)
    {
        var currentIndex = (int)Math.Round((ChartScrollSpeed - 1d) / .25d);
        var nextIndex = Math.Clamp(currentIndex + Math.Sign(direction), 0, ChartScrollSpeeds.Count - 1);
        ChartScrollSpeed = ChartScrollSpeeds[nextIndex];
    }

    [RelayCommand]
    private void RestartChart()
    {
        ChartPositionSeconds = 0;
        _lastChartFrame = Stopwatch.GetTimestamp();
    }

    private async Task LoadChartPreviewAsync(Chart? chart)
    {
        _chartPreviewCancellation?.Cancel();
        _chartPreviewCancellation?.Dispose();
        _chartPreviewCancellation = new();
        var token = _chartPreviewCancellation.Token;
        ChartPreview = null;
        if (chart is null || !chart.Exists || !File.Exists(chart.FilePath)) return;
        IsLoadingChartPreview = true;
        try
        {
            var preview = await Task.Run(() => chartPreviewBuilder.Build(chart.FilePath), token);
            token.ThrowIfCancellationRequested();
            ChartPreview = preview;
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) { StatusText = $"谱面预览失败：{exception.Message}"; }
        finally { if (!token.IsCancellationRequested) IsLoadingChartPreview = false; }
    }

    private void AdvanceChartPlayback(object? sender, EventArgs e)
    {
        if (!IsChartPlaying || ChartPreview is null) return;
        var now = Stopwatch.GetTimestamp();
        var elapsed = (now - _lastChartFrame) / (double)Stopwatch.Frequency * ChartPlaybackSpeed;
        _lastChartFrame = now;
        var previousSeconds = ChartPositionSeconds;
        var nextSeconds = previousSeconds + elapsed;
        // MediaPlayer.Position is intentionally not used as a frame-by-frame visual clock. Its
        // coarse updates, especially immediately after an asynchronous Open/Play, made the chart
        // alternate between holding and jumping during the first seconds. Stopwatch stays smooth;
        // seek/play operations still place music and visuals at the same explicit position.
        _isAdvancingChart = true;
        ChartPositionSeconds = Math.Min(ChartDurationSeconds, Math.Max(previousSeconds, nextSeconds));
        _isAdvancingChart = false;
        PlayChartSounds(previousSeconds, ChartPositionSeconds);
        if (ChartPositionSeconds >= ChartDurationSeconds) StopChartPlayback(preservePosition: true);
    }

    private void StopChartPlayback(bool preservePosition = false)
    {
        _chartTimer.Stop();
        _player.Pause();
        chartSounds.StopAll();
        IsChartPlaying = false;
        if (!IsChartPlayerOpen) _chartAudioReady = false;
        if (!preservePosition && ChartPositionSeconds > ChartDurationSeconds) ChartPositionSeconds = 0;
    }

    private async Task PrepareChartAudioAsync(Music music)
    {
        _chartAudioReady = false;
        if (music.Audio is null) return;
        try
        {
            var wave = await audio.DecodeToWaveAsync(music.Audio, paths.AudioCachePath, CancellationToken.None);
            if (!IsChartPlayerOpen || !ReferenceEquals(SelectedMusic, music)) return;
            _player.Open(new Uri(wave));
            _player.SpeedRatio = ChartPlaybackSpeed;
            _chartAudioReady = true;
            if (IsChartPlaying)
            {
                _player.Position = TimeSpan.FromSeconds(ChartPositionSeconds);
                _player.Play();
            }
        }
        catch (Exception exception) { StatusText = $"谱面画面可以播放，但音频准备失败：{exception.Message}"; }
    }

    private async Task PrepareChartGameResourcesAsync()
    {
        ChartUsesGameSounds = false;
        try
        {
            ChartUsesGameSounds = await chartSounds.PrepareAsync(CancellationToken.None);
            if (!IsChartPlayerOpen) ChartUsesGameSounds = false;
        }
        catch (OperationCanceledException) { }
        catch (Exception exception)
        {
            StatusText = $"谱面仍可播放，但游戏素材准备失败：{exception.Message}";
        }
    }

    private void RebuildChartSoundEvents(ChartPreview? preview)
    {
        _chartSoundEvents.Clear();
        if (preview is null) { _nextChartSoundEvent = 0; return; }
        for (var index = 0; index < preview.Notes.Count; index++)
        {
            var note = preview.Notes[index];
            var startSeconds = preview.SecondsAtTick(note.Tick);
            var wall = note.LaneKind is ChartPreviewLaneKind.LeftWall or ChartPreviewLaneKind.RightWall;
            switch (note.Kind)
            {
                case ChartPreviewNoteKind.Tap:
                    AddHit(startSeconds, wall, note.IsCritical);
                    break;
                case ChartPreviewNoteKind.Hold:
                    AddHit(startSeconds, wall, note.IsCritical);
                    var holdVoice = $"hold:{index}";
                    _chartSoundEvents.Add(new(startSeconds, ChartSoundAction.StartLoop,
                        ChartPreviewSoundKind.HoldLoop, holdVoice));
                    var endSeconds = Math.Max(startSeconds, preview.SecondsAtTick(note.EndTick) - .5d / 60d);
                    _chartSoundEvents.Add(new(endSeconds, ChartSoundAction.StopLoop,
                        ChartPreviewSoundKind.HoldLoop, holdVoice));
                    _chartSoundEvents.Add(new(endSeconds, ChartSoundAction.Play,
                        ChartPreviewSoundKind.HoldEnd));
                    break;
                case ChartPreviewNoteKind.Flick:
                    _chartSoundEvents.Add(new(startSeconds, ChartSoundAction.Play, ChartPreviewSoundKind.Flick));
                    if (note.IsCritical)
                        _chartSoundEvents.Add(new(startSeconds, ChartSoundAction.Play, ChartPreviewSoundKind.CriticalFlick));
                    break;
                case ChartPreviewNoteKind.Bell:
                    _chartSoundEvents.Add(new(startSeconds, ChartSoundAction.Play, ChartPreviewSoundKind.Bell));
                    break;
                // The arcade game has no timestamp-triggered bullet cue. Graze/damage sounds depend on player collision.
                case ChartPreviewNoteKind.Bullet:
                    break;
            }
        }
        foreach (var beam in preview.Lanes.Where(lane => lane.Kind == ChartPreviewLaneKind.Beam && lane.Points.Count > 0))
        {
            var start = preview.SecondsAtTick(beam.StartTick);
            var noticeVoice = $"beam-notice:{beam.Id}";
            var shotVoice = $"beam-shot:{beam.Id}";
            _chartSoundEvents.Add(new(Math.Max(0, start - 45d / 60d), ChartSoundAction.StartLoop,
                ChartPreviewSoundKind.BeamNotice, noticeVoice));
            _chartSoundEvents.Add(new(Math.Max(0, start - 10d / 60d), ChartSoundAction.StopLoop,
                ChartPreviewSoundKind.BeamNotice, noticeVoice));
            _chartSoundEvents.Add(new(Math.Max(0, start - 10d / 60d), ChartSoundAction.StartLoop,
                ChartPreviewSoundKind.BeamShot, shotVoice));
            _chartSoundEvents.Add(new(preview.SecondsAtTick(beam.EndTick), ChartSoundAction.StopLoop,
                ChartPreviewSoundKind.BeamShot, shotVoice));
        }
        _chartSoundEvents.Sort((left, right) =>
        {
            var byTime = left.Seconds.CompareTo(right.Seconds);
            return byTime != 0 ? byTime : left.Action.CompareTo(right.Action);
        });
        ResetChartSoundCursor(ChartPositionSeconds);

        void AddHit(double seconds, bool isWall, bool critical)
        {
            _chartSoundEvents.Add(new(seconds, ChartSoundAction.Play,
                isWall ? ChartPreviewSoundKind.Wall : ChartPreviewSoundKind.Tap));
            if (critical)
                _chartSoundEvents.Add(new(seconds, ChartSoundAction.Play,
                    isWall ? ChartPreviewSoundKind.CriticalWall : ChartPreviewSoundKind.CriticalTap));
        }
    }

    private void ResetChartSoundCursor(double seconds)
    {
        var low = 0;
        var high = _chartSoundEvents.Count;
        while (low < high)
        {
            var middle = low + (high - low) / 2;
            if (_chartSoundEvents[middle].Seconds <= seconds) low = middle + 1;
            else high = middle;
        }
        _nextChartSoundEvent = low;
    }

    private void PlayChartSounds(double previousSeconds, double currentSeconds)
    {
        while (_nextChartSoundEvent < _chartSoundEvents.Count)
        {
            var item = _chartSoundEvents[_nextChartSoundEvent];
            if (item.Seconds > currentSeconds) break;
            _nextChartSoundEvent++;
            if (item.Seconds > previousSeconds) ApplyChartSoundEvent(item);
        }
    }

    private void RestoreChartLoopsAt(double seconds)
    {
        var active = new Dictionary<string, ChartPreviewSoundKind>();
        foreach (var item in _chartSoundEvents)
        {
            if (item.Seconds > seconds) break;
            if (item.VoiceId is null) continue;
            if (item.Action == ChartSoundAction.StartLoop) active[item.VoiceId] = item.Kind;
            else if (item.Action == ChartSoundAction.StopLoop) active.Remove(item.VoiceId);
        }
        foreach (var pair in active) chartSounds.StartLoop(pair.Key, pair.Value);
    }

    private void ApplyChartSoundEvent(ChartTimedSoundEvent item)
    {
        switch (item.Action)
        {
            case ChartSoundAction.StopLoop when item.VoiceId is not null: chartSounds.StopLoop(item.VoiceId); break;
            case ChartSoundAction.Play: chartSounds.Play(item.Kind); break;
            case ChartSoundAction.StartLoop when item.VoiceId is not null: chartSounds.StartLoop(item.VoiceId, item.Kind); break;
        }
    }

    private async Task SaveSettingsQuietlyAsync()
    {
        try { await settings.SaveAsync(); }
        catch (Exception exception) { StatusText = $"设置保存失败：{exception.Message}"; }
    }

    private enum ChartSoundAction { StopLoop, Play, StartLoop }
    private sealed record ChartTimedSoundEvent(double Seconds, ChartSoundAction Action,
        ChartPreviewSoundKind Kind, string? VoiceId = null);

    private static string FormatTime(double seconds)
    {
        var value = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return value.TotalHours >= 1 ? value.ToString(@"h\:mm\:ss") : value.ToString(@"m\:ss");
    }

    [RelayCommand]
    private async Task ExportSelectedImageAsync()
    {
        var resource = SelectedMusic?.Jacket;
        if (resource is null) { StatusText = "当前乐曲没有可导出的封面。"; return; }
        var dialog = new SaveFileDialog { Filter = "PNG 图片|*.png", FileName = resource.Key + ".png" };
        if (dialog.ShowDialog() != true) return;
        try { await exporter.ExportImageAsync(resource, dialog.FileName, CancellationToken.None); StatusText = $"已导出：{dialog.FileName}"; }
        catch (Exception exception) { StatusText = $"导出失败：{exception.Message}"; }
    }

    [RelayCommand]
    private async Task ExportCardImageAsync()
    {
        var resource = SelectedCardVisual?.Resource ?? SelectedCard?.Image;
        if (resource is null) { StatusText = "当前卡片没有可导出的卡面资源。"; return; }
        var dialog = new SaveFileDialog { Filter = "PNG 图片|*.png", FileName = resource.Key + ".png" };
        if (dialog.ShowDialog() != true) return;
        try
        {
            await exporter.ExportImageAsync(resource, dialog.FileName, CancellationToken.None);
            StatusText = $"卡面已导出：{dialog.FileName}";
        }
        catch (Exception exception) { StatusText = $"卡面导出失败：{exception.Message}"; }
    }

    [RelayCommand]
    private async Task ExportCardAssetsAsync()
    {
        if (SelectedCard is null) return;
        var dialog = new OpenFolderDialog { Title = "选择卡片资源导出目录" };
        if (dialog.ShowDialog() != true) return;
        var target = Path.Combine(dialog.FolderName, $"card{SelectedCard.Id:D6}");
        try
        {
            foreach (var resource in new[] { SelectedCard.Image, SelectedCard.FullIllustration, SelectedCard.CharacterImage, SelectedCard.Icon }.OfType<ResourceReference>())
                await exporter.ExportImageAsync(resource, Path.Combine(target, resource.Key + ".png"), CancellationToken.None);
            StatusText = $"卡片资源已导出：{target}";
        }
        catch (Exception exception) { StatusText = $"卡片资源导出失败：{exception.Message}"; }
    }

    [RelayCommand]
    private async Task ExportCharacterExpressionAsync()
    {
        if (SelectedCharacterExpression is null) return;
        var dialog = new SaveFileDialog { Filter = "PNG 图片|*.png", FileName = SelectedCharacterExpression.Sprite.Name + ".png" };
        if (dialog.ShowDialog() != true) return;
        try
        {
            var image = await unityReader.ReadCharacterExpressionAsync(SelectedCharacterExpression.BundlePath,
                SelectedCharacterExpression.Sprite.PathId, CancellationToken.None);
            if (image is null) throw new InvalidDataException("无法组合该表情资源。");
            await File.WriteAllBytesAsync(dialog.FileName, image.PngBytes);
            StatusText = $"图片已导出：{dialog.FileName}";
        }
        catch (Exception exception) { StatusText = $"图片导出失败：{exception.Message}"; }
    }

    [RelayCommand]
    private async Task ExportIndexAsync()
    {
        if (_snapshot is null) { StatusText = "请先扫描游戏资源。"; return; }
        var dialog = new OpenFolderDialog { Title = "选择索引导出目录" };
        if (dialog.ShowDialog() != true) return;
        try
        {
            await exporter.ExportIndexJsonAsync(_snapshot, Path.Combine(dialog.FolderName, "ogktoolbox-index.json"), CancellationToken.None);
            await exporter.ExportMusicCsvAsync(_snapshot.EffectiveMusic, Path.Combine(dialog.FolderName, "music.csv"), CancellationToken.None);
            StatusText = $"JSON 与 CSV 索引已导出：{dialog.FolderName}";
        }
        catch (Exception exception) { StatusText = $"索引导出失败：{exception.Message}"; }
    }

    [RelayCommand]
    private async Task ExportChartRenderAssetManifestAsync()
    {
        if (!validator.TryValidate(GameRoot, out _, out var error))
        {
            StatusText = error ?? "请选择有效的游戏目录。";
            return;
        }

        var dialog = new SaveFileDialog
        {
            Filter = "谱面渲染资源清单|*.json",
            FileName = "note-render-assets.sddt150.json"
        };
        if (dialog.ShowDialog() != true) return;

        try
        {
            StatusText = "正在解析谱面渲染资源引用……";
            await exporter.ExportChartRenderAssetManifestJsonAsync(
                GameRoot, dialog.FileName, CancellationToken.None);
            StatusText = $"谱面渲染资源清单已导出：{dialog.FileName}";
        }
        catch (Exception exception)
        {
            StatusText = $"谱面渲染资源清单导出失败：{exception.Message}";
        }
    }

    [RelayCommand]
    private async Task ExportMusicFilesAsync()
    {
        if (SelectedMusic is null) return;
        var dialog = new OpenFolderDialog { Title = "选择乐曲资源导出目录" };
        if (dialog.ShowDialog() != true) return;
        var target = Path.Combine(dialog.FolderName, $"music{SelectedMusic.Id:D4}");
        try
        {
            await exporter.ExportFileAsync(SelectedMusic.Origin.SourcePath, Path.Combine(target, "Music.xml"), CancellationToken.None);
            foreach (var chart in SelectedMusic.Charts.Where(item => item.Exists))
                await exporter.ExportFileAsync(chart.FilePath, Path.Combine(target, Path.GetFileName(chart.FilePath)), CancellationToken.None);
            if (SelectedMusic.Jacket is not null)
                await exporter.ExportImageAsync(SelectedMusic.Jacket, Path.Combine(target, SelectedMusic.Jacket.Key + ".png"), CancellationToken.None);
            StatusText = $"乐曲资源已导出：{target}";
        }
        catch (Exception exception) { StatusText = $"乐曲导出失败：{exception.Message}"; }
    }

    [RelayCommand]
    private async Task ExportAudioWaveAsync()
    {
        if (SelectedMusic?.Audio is null) { StatusText = "当前乐曲没有可导出的音频。"; return; }
        var dialog = new SaveFileDialog { Filter = "WAV 音频|*.wav", FileName = $"music{SelectedMusic.Id:D4}.wav" };
        if (dialog.ShowDialog() != true) return;
        try
        {
            var wave = await audio.DecodeToWaveAsync(SelectedMusic.Audio, paths.AudioCachePath, CancellationToken.None);
            await exporter.ExportFileAsync(wave, dialog.FileName, CancellationToken.None);
            StatusText = $"音频已导出：{dialog.FileName}";
        }
        catch (Exception exception) { StatusText = $"音频导出失败：{exception.Message}"; }
    }

    [RelayCommand]
    private void OpenSource()
    {
        var path = SelectedMusic?.Origin.SourcePath ?? SelectedResource?.BundlePath;
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;
        Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
    }

    private async Task PopulateCollectionsAsync()
    {
        // 优先加载乐曲信息，快速显示首页
        RefreshMusic();
        SelectedMusic = Music.FirstOrDefault();
        OnPropertyChanged(nameof(MusicCount));
        OnPropertyChanged(nameof(ChartCount));
        
        // 标记卡片未加载，等待用户访问卡片页时再加载
        _cardsLoaded = false;
        
        await PopulateCharactersAsync(_snapshot);
        Replace(Resources, _snapshot is null ? Enumerable.Empty<GameResource>() : _snapshot.Resources.OrderBy(item => item.Kind).ThenBy(item => item.Key));
        Replace(Diagnostics, _snapshot?.Diagnostics ?? []);
        OnPropertyChanged(nameof(PackageCount)); OnPropertyChanged(nameof(ErrorCount));
        OnPropertyChanged(nameof(ResourceCount));
        NotifyEnvironmentStatus();
    }

    private async Task LoadCardsAsync()
    {
        if (_cardsLoaded || _snapshot is null) return;
        
        StatusText = "正在加载卡片数据...";
        
        // 在后台线程排序，避免阻塞 UI
        var sortedCards = await Task.Run(() => _snapshot.Cards.OrderBy(c => c.Id).ToList());
        
        Replace(Cards, sortedCards);
        BuildCardFilters();
        RefreshCards();
        _cardsLoaded = true;
        
        StatusText = $"已加载 {Cards.Count:N0} 张卡片";
    }

    private void RefreshEnvironmentStatus(object? sender = null, EventArgs? args = null)
    {
        var connected = ControllerConnectionProbe.IsConnected();
        IsControllerConnected = connected;
        ControllerStatus = connected ? "\u5DF2\u8FDE\u63A5" : "\u672A\u8FDE\u63A5";
    }

    private void NotifyEnvironmentStatus()
    {
        OnPropertyChanged(nameof(GameVersionDisplay));
        OnPropertyChanged(nameof(IsSegaToolsInstalled));
        OnPropertyChanged(nameof(SegaToolsStatus));
        OnPropertyChanged(nameof(HealthStatus));
    }


    private async Task PopulateCharactersAsync(LibrarySnapshot? snapshot)
    {
        _characterListCancellation?.Cancel();
        _characterListCancellation?.Dispose();
        _characterListCancellation = new();
        var cancellationToken = _characterListCancellation.Token;
        SelectedCharacter = null;
        Characters.Clear();
        IsLoadingCharacters = snapshot is not null;
        if (snapshot is null) return;

        try
        {
            var candidates = new List<(GameResource Resource, int ModelId)>();
            foreach (var resource in snapshot.Resources.Where(resource => resource.Origin.IsEffective
                && resource.Kind == ResourceKind.Character).DistinctBy(resource => resource.BundlePath, StringComparer.OrdinalIgnoreCase))
            {
                if (CharacterExpressionRules.TryGetModelIdFromBundle(resource.Key, out var modelId))
                    candidates.Add((resource, modelId));
            }

            var modelsWithExpressions = new ConcurrentDictionary<int, byte>();
            await Parallel.ForEachAsync(candidates, new ParallelOptions
            {
                CancellationToken = cancellationToken,
                MaxDegreeOfParallelism = 4
            }, async (candidate, token) =>
            {
                try
                {
                    var sprites = await unityReader.ListSpritesAsync(candidate.Resource.BundlePath, token);
                    if (sprites.Any(sprite => CharacterExpressionRules.IsFaceLayer(sprite.Name)))
                        modelsWithExpressions.TryAdd(candidate.ModelId, 0);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch
                {
                    // A broken optional bundle must not prevent the rest of the character list from loading.
                }
            });

            if (!ReferenceEquals(_snapshot, snapshot)) return;
            Replace(Characters, snapshot.Characters
                .Where(character => modelsWithExpressions.ContainsKey(character.ModelId))
                .OrderBy(character => character.Id));
            SelectedCharacter = Characters.FirstOrDefault();
        }
        catch (OperationCanceledException) { }
        finally
        {
            if (ReferenceEquals(_snapshot, snapshot)) IsLoadingCharacters = false;
        }
    }

    private void BuildCardFilters()
    {
        UnsubscribeFilterOptions(CardCharacterFilters);
        UnsubscribeFilterOptions(CardRarityFilters);
        UnsubscribeFilterOptions(CardSourceFilters);

        Replace(CardCharacterFilters, Cards
            .GroupBy(card => card.CharacterId)
            .OrderBy(group => group.Key)
            .Select(group => new SelectableFilterOption(group.Key.ToString(),
                $"{group.Select(card => card.CharacterName).FirstOrDefault(name => !string.IsNullOrWhiteSpace(name)) ?? "未知角色"} · {group.Key}")));
        Replace(CardRarityFilters, Cards.Select(card => card.Rarity)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
            .Select(value => new SelectableFilterOption(value, value)));
        Replace(CardSourceFilters, Cards.Select(card => card.Origin.PackageId)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
            .Select(value => new SelectableFilterOption(value, value)));

        SubscribeFilterOptions(CardCharacterFilters);
        SubscribeFilterOptions(CardRarityFilters);
        SubscribeFilterOptions(CardSourceFilters);
        NotifyCardFilterStateChanged();
    }

    private void RefreshCards()
    {
        var selectedCharacterIds = CardCharacterFilters.Where(item => item.IsSelected)
            .Select(item => int.Parse(item.Value)).ToHashSet();
        var selectedRarities = CardRarityFilters.Where(item => item.IsSelected)
            .Select(item => item.Value).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var selectedSources = CardSourceFilters.Where(item => item.IsSelected)
            .Select(item => item.Value).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var criteria = new CardFilterCriteria(selectedCharacterIds, selectedRarities, selectedSources);
        var previous = SelectedCard;
        Replace(FilteredCards, Cards.Where(criteria.Matches));
        SelectedCard = previous is not null && FilteredCards.Contains(previous) ? previous : FilteredCards.FirstOrDefault();
        NotifyCardFilterStateChanged();
    }

    private void SubscribeFilterOptions(IEnumerable<SelectableFilterOption> options)
    {
        foreach (var option in options) option.PropertyChanged += OnCardFilterOptionChanged;
    }

    private void UnsubscribeFilterOptions(IEnumerable<SelectableFilterOption> options)
    {
        foreach (var option in options) option.PropertyChanged -= OnCardFilterOptionChanged;
    }

    private void OnCardFilterOptionChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (!_isUpdatingCardFilters && args.PropertyName == nameof(SelectableFilterOption.IsSelected)) RefreshCards();
    }

    private void NotifyCardFilterStateChanged()
    {
        OnPropertyChanged(nameof(FilteredCardCount));
        OnPropertyChanged(nameof(CardCharacterFilterLabel));
        OnPropertyChanged(nameof(CardRarityFilterLabel));
        OnPropertyChanged(nameof(CardSourceFilterLabel));
        OnPropertyChanged(nameof(HasActiveCardFilters));
    }

    private static string FilterLabel(string title, IEnumerable<SelectableFilterOption> options)
    {
        var count = options.Count(item => item.IsSelected);
        return count == 0 ? $"{title} · 全部" : $"{title} · {count}";
    }

    private void RefreshMusic()
    {
        var source = _snapshot?.EffectiveMusic ?? [];
        if (!string.IsNullOrWhiteSpace(SearchText))
        {
            source = source.Where(item => item.Title.Contains(SearchText, StringComparison.CurrentCultureIgnoreCase)
                || item.Artist.Contains(SearchText, StringComparison.CurrentCultureIgnoreCase)
                || item.Id.ToString().Contains(SearchText, StringComparison.Ordinal)).ToArray();
        }
        Replace(Music, source);
    }

    private void PopulateCardVisuals(Card? card)
    {
        CardVisuals.Clear();
        if (card is not null)
        {
            AddCardVisual("卡片", card.Image, CardDisplayMode.FinalCard);
            AddCardVisual("完整插画", card.FullIllustration, CardDisplayMode.FullIllustration);
            AddCardVisual("透明人物", card.CharacterImage, CardDisplayMode.TransparentCharacter);
        }
        SelectCardVisualForCurrentMode();
    }

    private void AddCardVisual(string label, ResourceReference? resource, CardDisplayMode mode)
    {
        if (resource is not null) CardVisuals.Add(new(label, resource, mode));
    }

    private void SelectCardVisualForCurrentMode() =>
        SelectedCardVisual = CardVisuals.FirstOrDefault(item => item.Mode == SelectedCardDisplayMode);

    private static double OverallScanPercent(ScanProgress progress)
    {
        if (progress.Phase.Contains("枚举", StringComparison.Ordinal)) return progress.Percent * 0.42;
        if (progress.Phase.Contains("元数据", StringComparison.Ordinal)) return 42 + progress.Percent * 0.48;
        if (progress.Phase.Contains("索引", StringComparison.Ordinal)) return 90 + progress.Percent * 0.1;
        return progress.Phase.Contains("完成", StringComparison.Ordinal) ? 100 : progress.Percent;
    }

    private async Task LoadCharacterExpressionsAsync(Character? character)
    {
        var version = ++_expressionLoadVersion;
        _expressionLoadCancellation?.Cancel();
        _expressionLoadCancellation?.Dispose();
        _expressionLoadCancellation = new();
        var cancellationToken = _expressionLoadCancellation.Token;
        ResetPreview();
        var selected = character;
        _loadedExpressionCharacter = null;
        CharacterExpressions.Clear();
        SelectedCharacterExpression = null;
        IsLoadingCharacterExpressions = false;
        if (selected is null || _snapshot is null || selected.ModelId <= 0) return;

        IsLoadingCharacterExpressions = true;
        try
        {
            var prefix = $"anm_chara_{selected.ModelId:D6}";
            var bundles = _snapshot.Resources.Where(resource => resource.Origin.IsEffective
                && resource.Kind == ResourceKind.Character
                && resource.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToArray();
            foreach (var bundle in bundles)
            {
                var sprites = await unityReader.ListSpritesAsync(bundle.BundlePath, cancellationToken);
                if (version != _expressionLoadVersion || !ReferenceEquals(SelectedCharacter, selected)) return;
                foreach (var sprite in sprites.Where(sprite => CharacterExpressionRules.IsFaceLayer(sprite.Name)))
                    CharacterExpressions.Add(new($"{sprite.Name} · {bundle.Key}", bundle.BundlePath, sprite));
            }
            _loadedExpressionCharacter = selected;
            SelectedCharacterExpression = CharacterExpressions.FirstOrDefault();
            await PrefetchCharacterExpressionsAsync(CharacterExpressions.ToArray(), version, cancellationToken);
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) { StatusText = $"表情资源读取失败：{exception.Message}"; }
        finally { if (version == _expressionLoadVersion) IsLoadingCharacterExpressions = false; }
    }

    private async Task LoadExpressionPreviewAsync(CharacterExpression? expression)
    {
        var (version, cancellationToken) = BeginPreviewLoad();
        if (expression is null) return;
        if (expression.CachedImage is not null)
        {
            if (version == _previewVersion) PreviewImage = expression.CachedImage;
            return;
        }
        try
        {
            await Task.Delay(55, cancellationToken);
            var image = await unityReader.ReadCharacterExpressionAsync(expression.BundlePath, expression.Sprite.PathId, cancellationToken);
            if (image is null) return;
            var bitmap = expression.CachedImage ?? await Task.Run(() => CreateBitmap(image.PngBytes), cancellationToken);
            expression.CachedImage ??= bitmap;
            if (version == _previewVersion) PreviewImage = bitmap;
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) { if (version == _previewVersion) StatusText = $"表情预览失败：{exception.Message}"; }
    }

    private async Task PrefetchCharacterExpressionsAsync(
        IReadOnlyList<CharacterExpression> expressions,
        int characterVersion,
        CancellationToken cancellationToken)
    {
        await Parallel.ForEachAsync(expressions, new ParallelOptions
        {
            CancellationToken = cancellationToken,
            MaxDegreeOfParallelism = 2
        }, async (expression, token) =>
        {
            if (characterVersion != _expressionLoadVersion || expression.CachedImage is not null) return;
            try
            {
                var image = await unityReader.ReadCharacterExpressionAsync(expression.BundlePath, expression.Sprite.PathId, token);
                if (image is not null && characterVersion == _expressionLoadVersion)
                    expression.CachedImage = await Task.Run(() => CreateBitmap(image.PngBytes), token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch
            {
                // Prefetch is opportunistic; the foreground preview reports actionable failures.
            }
        });
    }

    private (int Version, CancellationToken Token) BeginPreviewLoad()
    {
        var version = ++_previewVersion;
        _previewCancellation?.Cancel();
        _previewCancellation?.Dispose();
        _previewCancellation = new();
        PreviewImage = null;
        return (version, _previewCancellation.Token);
    }

    private void ResetPreview() => BeginPreviewLoad();

    private async Task LoadPreviewAsync(ResourceReference? resource)
    {
        var (version, cancellationToken) = BeginPreviewLoad();
        if (resource is null) return;
        try
        {
            var image = await unityReader.ReadFirstImageAsync(resource.BundlePath, cancellationToken);
            if (image is null) return;
            var bitmap = await Task.Run(() => CreateBitmap(image.PngBytes), cancellationToken);
            if (version == _previewVersion) PreviewImage = bitmap;
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) { if (version == _previewVersion) StatusText = $"预览失败：{exception.Message}"; }
    }

    private async Task LoadBundleAsync(GameResource? resource)
    {
        var version = ++_bundleLoadVersion;
        _bundleLoadCancellation?.Cancel();
        _bundleLoadCancellation?.Dispose();
        _bundleLoadCancellation = new();
        var cancellationToken = _bundleLoadCancellation.Token;
        SelectedBundleImage = null;
        BundleImages.Clear();
        ResetPreview();
        if (resource is null || !unityReader.IsUnityFsBundle(resource.BundlePath)) return;
        try
        {
            var images = await unityReader.ListImagesAsync(resource.BundlePath, cancellationToken);
            if (version != _bundleLoadVersion || !ReferenceEquals(SelectedResource, resource)) return;
            foreach (var item in images) BundleImages.Add(item);
            SelectedBundleImage = BundleImages.FirstOrDefault();
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) { if (version == _bundleLoadVersion) StatusText = $"资源枚举失败：{exception.Message}"; }
    }

    private async Task LoadBundleImageAsync(UnityImageInfo? info)
    {
        var resource = SelectedResource;
        var (version, cancellationToken) = BeginPreviewLoad();
        if (resource is null || info is null) return;
        try
        {
            var image = await unityReader.ReadImageAsync(resource.BundlePath, info.PathId, cancellationToken);
            if (image is null) return;
            var bitmap = await Task.Run(() => CreateBitmap(image.PngBytes), cancellationToken);
            if (version == _previewVersion
                && ReferenceEquals(SelectedResource, resource)
                && ReferenceEquals(SelectedBundleImage, info)) PreviewImage = bitmap;
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) { if (version == _previewVersion) StatusText = $"纹理预览失败：{exception.Message}"; }
    }

    [RelayCommand]
    private async Task ExportUnityAssetGraphAsync()
    {
        var resource = SelectedResource;
        if (resource is null || !unityReader.IsUnityFsBundle(resource.BundlePath))
        {
            StatusText = "请选择一个 UnityFS 资源包。";
            return;
        }

        var safeName = string.Concat(resource.Key.Select(character =>
            Path.GetInvalidFileNameChars().Contains(character) ? '_' : character));
        var dialog = new SaveFileDialog
        {
            Filter = "Unity 资源引用图|*.unity-graph.json|JSON 文件|*.json",
            FileName = $"{safeName}.unity-graph.json"
        };
        if (dialog.ShowDialog() != true) return;
        try
        {
            await exporter.ExportUnityAssetGraphJsonAsync(resource.BundlePath, dialog.FileName, CancellationToken.None);
            StatusText = $"已导出只读资源引用图：{dialog.FileName}";
        }
        catch (Exception exception) { StatusText = $"资源引用图导出失败：{exception.Message}"; }
    }

    private static void Replace<T>(ObservableCollection<T> target, IEnumerable<T> source)
    {
        if (target is BulkObservableCollection<T> bulk)
        {
            bulk.ReplaceAll(source);
            return;
        }
        target.Clear();
        foreach (var item in source) target.Add(item);
    }

    private static BitmapImage CreateBitmap(byte[] pngBytes)
    {
        using var stream = new MemoryStream(pngBytes);
        var bitmap = new BitmapImage();
        bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad; bitmap.StreamSource = stream; bitmap.EndInit(); bitmap.Freeze();
        return bitmap;
    }
}
