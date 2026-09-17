using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace OGKToolBox.App.Services;

internal static class WindowBackdropService
{
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaWindowCornerPreference = 33;
    private const int DwmwaSystemBackdropType = 38;

    public static void Apply(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero) return;
        var dark = System.Windows.Application.Current.Resources["WindowBrush"] is SolidColorBrush brush && brush.Color.R < 100;
        var darkValue = dark ? 1 : 0;
        var rounded = 2;
        var mica = 2;
        _ = DwmSetWindowAttribute(handle, DwmwaUseImmersiveDarkMode, ref darkValue, sizeof(int));
        _ = DwmSetWindowAttribute(handle, DwmwaWindowCornerPreference, ref rounded, sizeof(int));
        _ = DwmSetWindowAttribute(handle, DwmwaSystemBackdropType, ref mica, sizeof(int));
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int valueSize);
}
