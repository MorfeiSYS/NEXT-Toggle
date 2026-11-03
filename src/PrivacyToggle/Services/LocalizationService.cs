using System.Globalization;
using System.Windows;
using PrivacyToggle.Models;

namespace PrivacyToggle.Services;

/// <summary>Applies the chosen UI language to the current thread and <see cref="Resources"/>.</summary>
public static class LocalizationService
{
    public static CultureInfo CurrentCulture { get; private set; } = CultureInfo.GetCultureInfo("en");

    public static void Apply(UiLanguage language)
    {
        var culture = language switch
        {
            UiLanguage.English => CultureInfo.GetCultureInfo("en"),
            UiLanguage.Russian => CultureInfo.GetCultureInfo("ru"),
            _ => ResolveSystemCulture(),
        };
        CurrentCulture = culture;
        CultureInfo.CurrentUICulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;
        PrivacyToggle.Properties.Resources.Culture = culture;
    }

    private static CultureInfo ResolveSystemCulture()
    {
        var ui = CultureInfo.CurrentUICulture;
        return ui.TwoLetterISOLanguageName.Equals("ru", StringComparison.OrdinalIgnoreCase)
            ? CultureInfo.GetCultureInfo("ru")
            : CultureInfo.GetCultureInfo("en");
    }

    public static string Format(string format, params object[] args) => string.Format(CurrentCulture, format, args);

    /// <summary>Restarts the tray app with Settings open (after a language change).</summary>
    public static void RestartApp()
    {
        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe)) return;
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe, "--show") { UseShellExecute = true });
        Application.Current.Shutdown();
    }
}
