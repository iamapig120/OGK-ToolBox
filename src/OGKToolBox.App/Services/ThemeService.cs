using System.Windows;
using Microsoft.Win32;

namespace OGKToolBox.App.Services;

public sealed class ThemeService
{
    public void Apply(AppTheme theme)
    {
        var resolved = theme == AppTheme.System ? ReadSystemTheme() : theme;
        var themeName = SystemParameters.HighContrast ? "HighContrast" : resolved.ToString();
        var dictionaries = System.Windows.Application.Current.Resources.MergedDictionaries;
        var oldTheme = dictionaries.FirstOrDefault(dictionary => dictionary.Source?.OriginalString.Contains("Theme.", StringComparison.Ordinal) == true);
        var newTheme = new ResourceDictionary
        {
            Source = new Uri($"Themes/Theme.{themeName}.xaml", UriKind.Relative)
        };
        if (oldTheme is null) dictionaries.Insert(0, newTheme);
        else dictionaries[dictionaries.IndexOf(oldTheme)] = newTheme;
        foreach (Window window in System.Windows.Application.Current.Windows) WindowBackdropService.Apply(window);
    }

    private static AppTheme ReadSystemTheme()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return key?.GetValue("AppsUseLightTheme") is int value && value == 0 ? AppTheme.Dark : AppTheme.Light;
    }
}
