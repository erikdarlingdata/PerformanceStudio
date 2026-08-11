using System;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using System.Linq;
using System.Threading.Tasks;
using PlanViewer.App.Services;
using PlanViewer.Core.Services;

namespace PlanViewer.App;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        // Before any window exists: route every TextBox clipboard operation through
        // the guarded helper so a locked clipboard can't crash the app (issue #415).
        TextBoxClipboardGuard.Register();

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new MainWindow();
        }

        // Entra MFA needs a parent window handle for the WAM broker, or the prompt never
        // appears and the connection fails with 0xwindow_handle_required (issue #425).
        // Registered once here because SqlAuthenticationProvider is process-wide, so this
        // covers every connection Studio opens without touching each call site. No-op off
        // Windows, where interactive auth uses the browser and needs no handle.
        EntraInteractiveAuth.Register(ActiveWindowHandle);

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
    /// The window that should own an Entra MFA prompt, resolved at the moment MSAL asks (issue #425).
    ///
    /// <para>Prefers whichever window is currently active over the main window, because a connection is
    /// usually triggered from the connection dialog — parenting the account picker to the main window behind
    /// it would let the picker appear behind the dialog the user is looking at. Falls back to the main window,
    /// then to <see cref="IntPtr.Zero"/>, which MSAL treats the same as no handle: the prompt fails rather
    /// than the app crashing, which is the right way round for an auth path.</para>
    ///
    /// <para>Resolved per call rather than captured once: a window's platform handle is not valid until the
    /// window has been sourced, so a handle read at startup can be zero even though a real window exists a
    /// moment later.</para>
    ///
    /// <para>Marshaled to the UI thread: MSAL invokes this from whatever thread SqlClient's token acquisition
    /// happens to run on, and <c>desktop.Windows</c> is a UI-thread-owned collection that the UI thread can
    /// mutate (a dialog opening or closing) mid-enumeration. Blocking on <see cref="Dispatcher.UIThread"/> is
    /// safe here because no connection path blocks the UI thread on auth — every open in the app is async. If
    /// the dispatcher can't deliver anyway (shutdown timing), Zero degrades to MSAL's normal no-handle
    /// failure instead of throwing from inside the auth callback.</para>
    /// </summary>
    private static IntPtr ActiveWindowHandle()
    {
        try
        {
            return Dispatcher.UIThread.CheckAccess()
                ? ActiveWindowHandleOnUIThread()
                : Dispatcher.UIThread.Invoke(ActiveWindowHandleOnUIThread);
        }
        catch
        {
            return IntPtr.Zero;
        }
    }

    private static IntPtr ActiveWindowHandleOnUIThread()
    {
        if (Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop)
            return IntPtr.Zero;

        var window = desktop.Windows.FirstOrDefault(w => w.IsActive) ?? desktop.MainWindow;

        return window?.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
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