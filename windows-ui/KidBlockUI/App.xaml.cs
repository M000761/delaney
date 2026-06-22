using System;
using System.Diagnostics;
using System.IO;
using System.Windows;

namespace KidBlockUI;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Register the Syncfusion v33 Community licence from a non-tracked source, then
        // make FluentDark the app-wide default style for Syncfusion AND standard WPF
        // controls. ApplyThemeAsDefaultStyle MUST be set before the window's
        // InitializeComponent runs, i.e. before MainWindow is constructed below.
        TryRegisterSyncfusionLicense();
        Syncfusion.SfSkinManager.SfSkinManager.ApplyThemeAsDefaultStyle = true;

        // StartupUri was removed from App.xaml so this single code path owns window
        // creation; behaviour matches the prior StartupUri="Views/MainWindow.xaml" (the
        // MainWindow ctor still boots the SSH LogTail on Loaded).
        new Views.MainWindow().Show();
    }

    // Syncfusion licence registration; the key stays out of source control.
    //
    // Resolution order:
    //   1. SYNCFUSION_LICENSE_KEY environment variable (process / user scope).
    //   2. a syncfusion-license.key file next to the executable.
    //   3. a syncfusion-license.key file walked up toward the project root (so a key
    //      dropped beside the .csproj is found from a bin/... build output too).
    // With no key found the controls render in Syncfusion trial mode (a trial banner),
    // which is harmless; drop the key into one of the sources above to clear it. The key
    // file is gitignored so it can never be committed.
    private static void TryRegisterSyncfusionLicense()
    {
        try
        {
            string? key = Environment.GetEnvironmentVariable("SYNCFUSION_LICENSE_KEY");
            string source = "env:SYNCFUSION_LICENSE_KEY";

            if (string.IsNullOrWhiteSpace(key))
            {
                var keyFile = FindLicenceKeyFile();
                if (keyFile is not null)
                {
                    key = File.ReadAllText(keyFile).Trim();
                    source = keyFile;
                }
            }

            if (string.IsNullOrWhiteSpace(key))
            {
                Debug.WriteLine("Syncfusion: no licence found (SYNCFUSION_LICENSE_KEY unset and no syncfusion-license.key). Controls run in trial mode.");
                return;
            }

            Syncfusion.Licensing.SyncfusionLicenseProvider.RegisterLicense(key);
            Debug.WriteLine($"Syncfusion: licence registered from {source} ({key.Length} chars)");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Syncfusion: licence registration failed; continuing in trial mode: {ex.Message}");
        }
    }

    private static string? FindLicenceKeyFile()
    {
        const string fileName = "syncfusion-license.key";
        var baseDir = AppContext.BaseDirectory;
        var direct = Path.Combine(baseDir, fileName);
        if (File.Exists(direct)) return direct;

        var dir = new DirectoryInfo(baseDir);
        for (int i = 0; i < 8 && dir is not null; i++, dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, fileName);
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }
}
