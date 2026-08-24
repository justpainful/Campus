using Campus.Desktop.Design;
using Campus.Domain;
using Campus.Vault;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;

namespace Campus.Desktop;

public partial class App : Application
{
    private Window? _window;

    public App()
    {
        // Startup failures in a XAML app surface as a stowed exception with no managed stack, so
        // everything from here on is written to a log the moment it happens.
        AppDomain.CurrentDomain.FirstChanceException += (_, e) => Diagnostics.Log("first-chance", e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Diagnostics.Log("domain", e.ExceptionObject as Exception);

        try
        {
            InitializeComponent();
        }
        catch (Exception ex)
        {
            Diagnostics.Log("InitializeComponent", ex);
            throw;
        }

        UnhandledException += OnUnhandledException;
    }

    /// <summary>Application-wide services. Resolved through <see cref="GetService{T}"/>.</summary>
    public static IServiceProvider Services { get; private set; } = null!;

    public static T GetService<T>() where T : class => Services.GetRequiredService<T>();

    public static Window MainWindow { get; private set; } = null!;

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
        {
            Launch();
        }
        catch (Exception ex)
        {
            Diagnostics.Log("OnLaunched", ex);
            throw;
        }
    }

    private void Launch()
    {
        Services = ConfigureServices();

        var theme = GetService<ThemeService>();

        _window = new MainWindow(StartupDestination());
        MainWindow = _window;
        theme.Initialise(_window.DispatcherQueue);

        if (_window.Content is FrameworkElement root)
            theme.RegisterRoot(root);

        _window.Activate();
    }

    /// <summary>
    /// Reads "--open &lt;destination&gt;" from the command line. This is what a shortcut, a jump
    /// list entry or a deep link uses to land straight on a destination instead of Home.
    /// </summary>
    private static string? StartupDestination()
    {
        var args = Environment.GetCommandLineArgs();
        for (var i = 1; i < args.Length - 1; i++)
        {
            if (args[i] is "--open" or "-o") return args[i + 1];
        }
        return null;
    }

    private static ServiceProvider ConfigureServices()
    {
        var services = new ServiceCollection();

        // Design system
        services.AddSingleton<ThemeService>();
        services.AddSingleton<ThemeResolver>();

        // Vault. The platform protector is added once the Windows Hello implementation lands;
        // until then the vault still works through its recovery key.
        services.AddSingleton(_ => VaultPaths.Default());
        services.AddSingleton(sp => new CampusVault(sp.GetRequiredService<VaultPaths>()));

        // Settings, replaced by the persisted copy once the workspace database is open.
        services.AddSingleton<WorkspaceSettings>();

        return services.BuildServiceProvider();
    }

    private void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        Diagnostics.Log("unhandled", e.Exception);

        // A crash must never leave decrypted material in memory for a debugger to pick up.
        try { Services?.GetService<CampusVault>()?.Lock(); }
        catch { /* the process is already going down */ }
    }
}
