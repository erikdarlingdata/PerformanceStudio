using System;
using Microsoft.Data.SqlClient;

namespace PlanViewer.Core.Services;

/// <summary>
/// Makes <c>Authentication=ActiveDirectoryInteractive</c> (Microsoft Entra MFA) work on Windows by giving
/// MSAL a parent window handle.
///
/// <para>Why this has to exist: current <c>Microsoft.Data.SqlClient</c> routes interactive Entra auth through
/// the Windows <b>WAM broker</b>, and WAM requires the calling application to supply the HWND that will own
/// the account picker. An application that never supplies one does not get a prompt — it gets
/// <c>0xwindow_handle_required</c> / "A window handle must be configured" and the connection fails outright.
/// Studio set the authentication mode but never registered a provider, so Entra MFA was broken for every
/// Windows user rather than for some particular tenant (issue #425, reported via PerformanceMonitor#2184;
/// Studio 1.4.3 worked because it predates WAM being the default and fell back to a browser).</para>
///
/// <para>Registration is process-wide. <see cref="SqlAuthenticationProvider.SetProvider"/> installs against
/// the authentication METHOD, not against a connection, so one call at startup covers every
/// <c>SqlConnection</c> the process opens — which is what we want here, because Studio opens connections from
/// several unrelated places (the connection dialog, the query session control, the schema service) and
/// threading a handle through all of them would be a change every future call site could forget to make.</para>
/// </summary>
public static class EntraInteractiveAuth
{
    private static readonly object Gate = new();
    private static bool _registered;

    /// <summary>
    /// Registers the interactive-auth provider, resolving the parent window through
    /// <paramref name="parentWindowHandleProvider"/> at the moment MSAL asks for it.
    ///
    /// <para>The handle is fetched per prompt rather than captured once, deliberately: a window's platform
    /// handle is not valid until the window has been sourced, and the right parent is whichever window is
    /// actually in front when the user triggers a connection — not necessarily the one that existed at
    /// startup.</para>
    ///
    /// <para>Windows only. WAM does not exist on macOS or Linux, where interactive auth already works through
    /// the system browser and needs no handle at all, so registering a handle-supplying provider there would
    /// add a failure mode to platforms that currently work. Returns false when it did not register, so a
    /// caller can tell "not applicable" from "done" without duplicating the OS check.</para>
    /// </summary>
    /// <param name="parentWindowHandleProvider">
    /// Returns the owning window handle, or <see cref="IntPtr.Zero"/> when no window is available yet.
    /// </param>
    /// <returns>True if the provider was registered by this call; false if not applicable or already done.</returns>
    public static bool Register(Func<IntPtr> parentWindowHandleProvider)
    {
        ArgumentNullException.ThrowIfNull(parentWindowHandleProvider);

        if (!OperatingSystem.IsWindows())
            return false;

        lock (Gate)
        {
            if (_registered)
                return false;

            var provider = new ActiveDirectoryAuthenticationProvider();

            /* Func<object> rather than Func<IntPtr> is MSAL's shape — it takes an Android Activity on
               mobile and an HWND on Windows. Boxing the IntPtr is the intended usage. */
            provider.SetParentActivityOrWindowFunc(() => parentWindowHandleProvider());

            SqlAuthenticationProvider.SetProvider(SqlAuthenticationMethod.ActiveDirectoryInteractive, provider);

            _registered = true;
            return true;
        }
    }

    /// <summary>
    /// Whether interactive Entra auth is expected to work in this process: either a provider has been
    /// registered, or we are not on Windows and therefore never needed one. False means a prompt would fail
    /// with <c>0xwindow_handle_required</c> — which is the case a windowless host (the CLI) should surface as
    /// a clear message instead of letting MSAL produce that error.
    /// </summary>
    public static bool IsSupported => _registered || !OperatingSystem.IsWindows();
}
