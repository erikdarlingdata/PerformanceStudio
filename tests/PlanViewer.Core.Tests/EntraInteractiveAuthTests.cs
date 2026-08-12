using PlanViewer.Core.Services;

namespace PlanViewer.Core.Tests;

/// <summary>
/// Issue #425: interactive Entra auth needs a parent window handle on Windows, because SqlClient routes it
/// through the WAM broker. These pin the parts that are verifiable without a tenant — the OS gating and the
/// contract the windowless CLI depends on. Whether the picker actually authenticates can only be established
/// against a real Entra tenant, which is what the reporter offered to do.
///
/// <para>Shares a collection with <see cref="CliConnectionResolverTests"/>: registration here flips the
/// process-wide state that the CLI refusal test keys off, so the two classes must not run in parallel.</para>
/// </summary>
[Collection("EntraInteractiveAuth process-wide state")]
public class EntraInteractiveAuthTests
{
    [Fact]
    public void Register_RejectsANullHandleProvider()
    {
        /* The whole point of the type is supplying a handle; accepting null would register a provider that
           fails at prompt time instead of at wiring time, which is the harder bug to find. */
        Assert.Throws<ArgumentNullException>(() => EntraInteractiveAuth.Register(null!));
    }

    [Fact]
    public void OffWindows_RegistrationIsSkippedButInteractiveAuthIsStillConsideredSupported()
    {
        /* macOS and Linux have no WAM broker: interactive auth goes through the system browser and needs no
           handle, so registering a handle-supplying provider there would add a failure mode to the platforms
           that currently work. Register must decline, and IsSupported must still be TRUE — otherwise the CLI
           guard would refuse `--auth entra` on exactly the platforms where it is fine. */
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Windows has WAM; this pins the non-Windows contract.");

        var called = false;
        var registered = EntraInteractiveAuth.Register(() => { called = true; return IntPtr.Zero; });

        Assert.False(registered, "off Windows there is nothing to register");
        Assert.False(called, "the handle provider must not even be consulted off Windows");
        Assert.True(EntraInteractiveAuth.IsSupported,
            "browser-based interactive auth works off Windows, so the CLI must not refuse it there");
    }

    [Fact]
    public void OnWindows_RegistersOnceAndIsIdempotent()
    {
        /* SqlAuthenticationProvider.SetProvider is process-wide, so a second registration would silently
           replace the first — and with several entry points able to call this (the app today, the SSMS
           extension later) "first one wins, later ones are no-ops" is the contract worth pinning. */
        Assert.SkipUnless(OperatingSystem.IsWindows(), "WAM registration only happens on Windows.");

        var first = EntraInteractiveAuth.Register(() => IntPtr.Zero);
        var second = EntraInteractiveAuth.Register(() => new IntPtr(1234));

        Assert.False(second, "a second Register must be a no-op rather than replacing the provider");
        Assert.True(EntraInteractiveAuth.IsSupported);

        /* first is only true when this test observed the very first registration in the process; another test
           or the host may legitimately have gotten there first, so it is not asserted either way. */
        _ = first;
    }
}
