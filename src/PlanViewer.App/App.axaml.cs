using System;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
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

        // Register the .sqlplan association (Windows/Linux) off the UI thread so it
        // never delays first paint. Best-effort; the OS then routes double-clicks to
        // the existing argv/pipe open path. No-op on macOS (handled by Info.plist).
        Task.Run(FileAssociationService.RegisterForCurrentExecutable);

        base.OnFrameworkInitializationCompleted();
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