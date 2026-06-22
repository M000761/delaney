using System;
using System.Diagnostics;
using System.IO;
using System.Windows;

namespace KidBlockUI;

public partial class App : Application
{
    // DM18 (DM13 program, Stage 4): the system-tray surface. One NotifyIcon per process, owned
    // here because the tray (and its Exit action) outlive any single window. Forms types are
    // fully-qualified throughout so the WinForms implicit usings stay disabled (csproj) and never
    // collide with the WPF System.Windows names the rest of the app uses unqualified.
    private System.Windows.Forms.NotifyIcon? _trayIcon;
    private bool _minimiseHintShown;

    // True once the tray icon is live. MainWindow only hides-to-tray when there is a tray to
    // restore from, so a tray-init failure can never strand the window with no way back.
    public bool TrayActive => _trayIcon is not null;

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

        // After the window exists so RestoreMainWindow has a target to show.
        TryInitTrayIcon();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // Dispose removes the icon from the notification area; an undisposed NotifyIcon lingers
        // as a ghost icon until the user next hovers over it.
        _trayIcon?.Dispose();
        _trayIcon = null;
        base.OnExit(e);
    }

    // ===================== Tray surface (DM18) =====================

    private void TryInitTrayIcon()
    {
        try
        {
            _trayIcon = new System.Windows.Forms.NotifyIcon
            {
                Icon = ResolveTrayIcon(),
                Text = "KidBlock",
                Visible = true,
                ContextMenuStrip = BuildTrayMenu(),
            };
            // Double-clicking the tray icon restores the window (the conventional affordance,
            // alongside the Show context-menu item).
            _trayIcon.DoubleClick += (_, _) => RestoreMainWindow();
        }
        catch (Exception ex)
        {
            // Headless / no shell notification area: carry on without a tray (TrayActive stays
            // false, so the window keeps its normal minimise-to-taskbar behaviour).
            Debug.WriteLine($"NotifyIcon: tray init failed; running without a tray surface: {ex.Message}");
            _trayIcon = null;
        }
    }

    private System.Windows.Forms.ContextMenuStrip BuildTrayMenu()
    {
        var menu = new System.Windows.Forms.ContextMenuStrip();

        var show = new System.Windows.Forms.ToolStripMenuItem("Show");
        show.Click += (_, _) => RestoreMainWindow();

        var exit = new System.Windows.Forms.ToolStripMenuItem("Exit");
        exit.Click += (_, _) => Shutdown();

        menu.Items.Add(show);
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add(exit);

        // Theme the native (GDI) menu from the SAME Themes/Theme.xaml tokens the WPF shell uses
        // (PaneHeaderBg surface, TextPrimary text, BorderSubtle edge, PaneBg as the hover), so
        // the tray menu matches FluentDark with no duplicated palette. The fallbacks mirror the
        // Theme.xaml values byte for byte in case a key fails to resolve. No Views/*.xaml is
        // touched, so the PaletteLint guard stays green.
        var surface = ThemeColor("PaneHeaderBg", System.Drawing.Color.FromArgb(0x2D, 0x2D, 0x30));
        var text    = ThemeColor("TextPrimary",  System.Drawing.Color.FromArgb(0xDD, 0xDD, 0xDD));
        var edge    = ThemeColor("BorderSubtle", System.Drawing.Color.FromArgb(0x3F, 0x3F, 0x46));
        var hover   = ThemeColor("PaneBg",       System.Drawing.Color.FromArgb(0x25, 0x25, 0x26));
        menu.BackColor = surface;
        menu.ForeColor = text;
        menu.Renderer = new System.Windows.Forms.ToolStripProfessionalRenderer(
            new TrayColorTable(surface, edge, hover));
        return menu;
    }

    // Restore + focus the main window from the tray (Show item and the icon double-click).
    public void RestoreMainWindow()
    {
        if (MainWindow is not { } w) return;
        w.Show();                       // undo the hide-to-tray
        w.WindowState = WindowState.Normal;
        w.Activate();                   // bring to foreground + focus
    }

    // Called by MainWindow when it hides to the tray. Shows a one-time balloon so the first
    // minimise does not look like the app vanished; this also exercises the notification hook
    // the milestone asked to wire if useful.
    public void NotifyMinimisedToTray()
    {
        if (_trayIcon is null || _minimiseHintShown) return;
        _minimiseHintShown = true;
        _trayIcon.BalloonTipTitle = "KidBlock is still running";
        _trayIcon.BalloonTipText =
            "Minimised to the tray. Double-click the icon (or right-click for Show / Exit) to reopen.";
        _trayIcon.ShowBalloonTip(3000);
    }

    private static System.Drawing.Color ThemeColor(string key, System.Drawing.Color fallback)
    {
        if (Current?.TryFindResource(key) is System.Windows.Media.SolidColorBrush b)
        {
            var c = b.Color;
            return System.Drawing.Color.FromArgb(c.A, c.R, c.G, c.B);
        }
        return fallback;
    }

    private static System.Drawing.Icon ResolveTrayIcon()
    {
        // No bespoke .ico ships with the app, so reuse the executable's own associated icon;
        // fall back to the shell's generic application icon if extraction fails.
        try
        {
            var exe = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(exe))
            {
                var ico = System.Drawing.Icon.ExtractAssociatedIcon(exe);
                if (ico is not null) return ico;
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"NotifyIcon: icon extraction failed; using SystemIcons.Application: {ex.Message}");
        }
        return System.Drawing.SystemIcons.Application;
    }

    // Dark colour table for the native tray menu, fed the resolved Theme.xaml colours so the
    // menu surface / border / hover all track the WPF palette.
    private sealed class TrayColorTable : System.Windows.Forms.ProfessionalColorTable
    {
        private readonly System.Drawing.Color _surface, _edge, _hover;

        public TrayColorTable(System.Drawing.Color surface, System.Drawing.Color edge, System.Drawing.Color hover)
        {
            _surface = surface;
            _edge = edge;
            _hover = hover;
            UseSystemColors = false;
        }

        public override System.Drawing.Color ToolStripDropDownBackground => _surface;
        public override System.Drawing.Color ImageMarginGradientBegin => _surface;
        public override System.Drawing.Color ImageMarginGradientMiddle => _surface;
        public override System.Drawing.Color ImageMarginGradientEnd => _surface;
        public override System.Drawing.Color MenuBorder => _edge;
        public override System.Drawing.Color MenuItemBorder => _edge;
        public override System.Drawing.Color MenuItemSelected => _hover;
        public override System.Drawing.Color MenuItemSelectedGradientBegin => _hover;
        public override System.Drawing.Color MenuItemSelectedGradientEnd => _hover;
        public override System.Drawing.Color SeparatorDark => _edge;
        public override System.Drawing.Color SeparatorLight => _edge;
    }

    // ===================== Syncfusion licence (DM14) =====================

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
