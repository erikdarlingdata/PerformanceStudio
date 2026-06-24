using System;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using System.Linq;
using System.Threading.Tasks;
using PlanViewer.App.Services;

namespace PlanViewer.App;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new MainWindow();
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            var iconPath = System.IO.Path.Combine(AppContext.BaseDirectory, "EDD.icns");
            MacOSDockIcon.SetDockIcon(iconPath);
        }

        // macOS delivers a double-clicked .sqlplan via an activation event — the path
        // is NOT passed in argv as it is on Windows/Linux. Subscribe so Finder "Open"
        // (and drag-onto-dock) loads the plan through the same path as any other open.
        if (this.TryGetFeature<IActivatableLifetime>() is { } activatable)
            activatable.Activated += OnAppActivated;

        // Register the .sqlplan association (Windows/Linux) off the UI thread so it
        // never delays first paint. Best-effort; the OS then routes double-clicks to
        // the existing argv/pipe open path. No-op on macOS (handled by Info.plist).
        Task.Run(FileAssociationService.RegisterForCurrentExecutable);

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>
    /// Handles macOS file-open activations (<see cref="ActivationKind.File"/>). The
    /// opened plan paths arrive here via the activation event rather than argv, so we
    /// route them into the existing open path on the main window.
    /// </summary>
    private void OnAppActivated(object? sender, ActivatedEventArgs e)
    {
        if (e is not FileActivatedEventArgs fileArgs)
            return;

        var paths = fileArgs.Files
            .Select(f => f.TryGetLocalPath())
            .Where(p => !string.IsNullOrEmpty(p))
            .Select(p => p!)
            .ToList();

        if (paths.Count > 0
            && ApplicationLifetime is IClassicDesktopStyleApplicationLifetime { MainWindow: MainWindow mainWindow })
        {
            mainWindow.OpenFiles(paths);
        }
    }

    private void OnAboutClicked(object? sender, System.EventArgs e)
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop
            && desktop.MainWindow is Window mainWindow)
        {
            var about = new AboutWindow();
            about.ShowDialog(mainWindow);
        }
    }
}