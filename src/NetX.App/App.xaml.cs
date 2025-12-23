using System.Diagnostics;
using System.Security.Principal;
using System.Windows;
using NetX.Core.Helpers;
using NetX.Core.Data;

namespace NetX.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Set process priority to High for best performance
        SetHighPriority();

        // Initialize database on first run
        InitializeDatabase();

        // Check and set language
        InitializeLanguage();
    }

    private void SetHighPriority()
    {
        try
        {
            using var process = Process.GetCurrentProcess();
            process.PriorityClass = ProcessPriorityClass.High;
        }
        catch
        {
            // Ignore if cannot set priority
        }
    }

    private void InitializeDatabase()
    {
        try
        {
            DatabaseService.Instance.Initialize();
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"Failed to initialize database: {ex.Message}",
                "NetX - Error",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private void InitializeLanguage()
    {
        // Get system language
        var culture = System.Globalization.CultureInfo.CurrentUICulture;
        var langCode = culture.TwoLetterISOLanguageName;

        // Set Thai if system is Thai, otherwise English
        string langFile = langCode == "th" ? "Languages/th-TH.xaml" : "Languages/en-US.xaml";

        try
        {
            var dict = new ResourceDictionary
            {
                Source = new Uri(langFile, UriKind.Relative)
            };

            // Remove old language dictionary and add new one
            var oldDict = Resources.MergedDictionaries
                .FirstOrDefault(d => d.Source?.OriginalString.Contains("Languages/") == true);

            if (oldDict != null)
            {
                Resources.MergedDictionaries.Remove(oldDict);
            }

            Resources.MergedDictionaries.Add(dict);
        }
        catch
        {
            // Keep default language if failed
        }
    }

    public static void ChangeLanguage(string cultureCode)
    {
        string langFile = cultureCode == "th-TH" ? "Languages/th-TH.xaml" : "Languages/en-US.xaml";

        var dict = new ResourceDictionary
        {
            Source = new Uri(langFile, UriKind.Relative)
        };

        var oldDict = Current.Resources.MergedDictionaries
            .FirstOrDefault(d => d.Source?.OriginalString.Contains("Languages/") == true);

        if (oldDict != null)
        {
            Current.Resources.MergedDictionaries.Remove(oldDict);
        }

        Current.Resources.MergedDictionaries.Add(dict);
    }

    public static bool IsRunAsAdmin()
    {
        var identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        return principal.IsInRole(WindowsBuiltInRole.Administrator);
    }
}
