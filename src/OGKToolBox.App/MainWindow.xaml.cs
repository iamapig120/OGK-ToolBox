using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using OGKToolBox.App.ViewModels;
using OGKToolBox.App.Services;

namespace OGKToolBox.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private ChartPlayerWindow? _chartPlayerWindow;
    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        DataContext = _viewModel = viewModel;
        SourceInitialized += (_, _) => WindowBackdropService.Apply(this);
        Loaded += async (_, _) =>
        {
            _viewModel.PropertyChanged += OnViewModelPropertyChanged;
            await _viewModel.InitializeAsync();
        };
        Closed += (_, _) => _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(MainViewModel.IsChartPlayerOpen)) return;
        Dispatcher.InvokeAsync(() =>
        {
            if (_viewModel.IsChartPlayerOpen)
            {
                if (_chartPlayerWindow is { IsVisible: true }) { _chartPlayerWindow.Activate(); return; }
                _chartPlayerWindow = new ChartPlayerWindow(_viewModel) { Owner = this };
                _chartPlayerWindow.Closed += (_, _) => _chartPlayerWindow = null;
                _chartPlayerWindow.Show();
            }
            else if (_chartPlayerWindow is not null)
            {
                _chartPlayerWindow.Close();
            }
        });
    }

    private void CycleCharacterExpressionItem(object sender, MouseWheelEventArgs e)
    {
        if (FindAncestor<ComboBox>(e.OriginalSource as DependencyObject) is { IsDropDownOpen: true }) return;
        _viewModel.CycleCharacterExpressionCommand.Execute(e.Delta < 0 ? 1 : -1);
        e.Handled = true;
    }

    private static T? FindAncestor<T>(DependencyObject? current) where T : DependencyObject
    {
        while (current is not null)
        {
            if (current is T match) return match;
            current = VisualTreeHelper.GetParent(current);
        }
        return null;
    }

    private void CycleImageViewerItem(object sender, MouseWheelEventArgs e)
    {
        if (!_viewModel.IsImageViewerOpen) return;
        _viewModel.CycleImageViewerCommand.Execute(e.Delta < 0 ? 1 : -1);
        e.Handled = true;
    }
}
