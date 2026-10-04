using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using DochkaDock.Services;

namespace DochkaDock;

public partial class App : Application
{
    // Prevents a second copy of the dock from launching (e.g. double-clicking
    // the autostart entry while it's already running from the tray).
    private static Mutex? _singleInstanceMutex;

    private TrayIconManager? _tray;
    private MainWindow? _mainWindow;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        DispatcherUnhandledException += OnDispatcherUnhandledException;

        _singleInstanceMutex = new Mutex(initiallyOwned: true, "DochkaDock.SingleInstance", out var createdNew);
        if (!createdNew)
        {
            Shutdown();
            return;
        }

        // Before the window exists, so the very first paint is already in
        // the saved language instead of flashing English then switching.
        var startupConfig = new ConfigService().Load();
        LocalizationService.Instance.SetLanguage(startupConfig.Language);
        ThemeService.Instance.SetTheme(ThemeService.Parse(startupConfig.Theme));

        _mainWindow = new MainWindow();
        _mainWindow.Show();

        _tray = new TrayIconManager(_mainWindow);
    }

    // A single bad interaction (a bug in a hover/drag/click handler) should
    // never take down a background tray app. Log it and keep running instead
    // of letting the default behavior (crash to desktop) kick in.
    private static void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "DochkaDock");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "error.log"),
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {e.Exception}\n\n");
        }
        catch
        {
            // If we can't even log, there's nothing more we can safely do.
        }

        e.Handled = true;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _tray?.Dispose();
        _singleInstanceMutex?.ReleaseMutex();
        base.OnExit(e);
    }
}
