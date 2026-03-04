using System;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

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