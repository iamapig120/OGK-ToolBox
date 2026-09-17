using System.ComponentModel;
using System.Windows;
using System.Windows.Media.Animation;
using OGKToolBox.App.Services;
using OGKToolBox.App.ViewModels;

namespace OGKToolBox.App;

public partial class ChartPlayerWindow : Window
{
    private readonly MainViewModel _viewModel;
    private bool _closingFromViewModel;
    private bool _controlsExpanded = true;
    private int _controlsAnimationVersion;

    public ChartPlayerWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        DataContext = _viewModel = viewModel;
        SourceInitialized += (_, _) => WindowBackdropService.Apply(this);
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        Loaded += (_, _) => SetControlsExpanded(!_viewModel.IsChartPlaying, animate: false);
        Closed += OnClosed;
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.IsChartPlaying))
        {
            Dispatcher.InvokeAsync(() => SetControlsExpanded(!_viewModel.IsChartPlaying));
            return;
        }
        if (e.PropertyName == nameof(MainViewModel.IsChartPlayerOpen) && !_viewModel.IsChartPlayerOpen)
        {
            _closingFromViewModel = true;
            Dispatcher.InvokeAsync(Close);
        }
    }

    private void ToggleControlsClick(object sender, RoutedEventArgs e) =>
        SetControlsExpanded(!_controlsExpanded);

    private void SetControlsExpanded(bool expanded, bool animate = true)
    {
        _controlsExpanded = expanded;
        var version = ++_controlsAnimationVersion;
        ControlsToggleGlyph.Text = expanded ? "›" : "‹";
        ControlsToggle.ToolTip = expanded ? "收起控制栏" : "展开控制栏";
        ControlsPanel.BeginAnimation(OpacityProperty, null);

        if (!animate)
        {
            ControlsPanel.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
            ControlsPanel.Opacity = expanded ? 1 : 0;
            return;
        }

        var easing = new CubicEase { EasingMode = EasingMode.EaseOut };
        if (expanded)
        {
            ControlsPanel.Visibility = Visibility.Visible;
            ControlsPanel.BeginAnimation(OpacityProperty,
                new DoubleAnimation(ControlsPanel.Opacity, 1, TimeSpan.FromMilliseconds(180))
                { EasingFunction = easing });
            return;
        }

        var animation = new DoubleAnimation(ControlsPanel.Opacity, 0, TimeSpan.FromMilliseconds(150))
        { EasingFunction = easing };
        animation.Completed += (_, _) =>
        {
            if (version == _controlsAnimationVersion && !_controlsExpanded)
                ControlsPanel.Visibility = Visibility.Collapsed;
        };
        ControlsPanel.BeginAnimation(OpacityProperty, animation);
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        if (!_closingFromViewModel && _viewModel.IsChartPlayerOpen)
            _viewModel.CloseChartPlayerCommand.Execute(null);
    }
}
