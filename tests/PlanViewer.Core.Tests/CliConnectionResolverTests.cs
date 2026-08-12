using PlanViewer.Cli.Commands;
using PlanViewer.Core.Interfaces;
using PlanViewer.Core.Services;

namespace PlanViewer.Core.Tests;

/// <summary>
/// Issue #425: the CLI accepts --auth entra but has no window to hand the WAM broker, so
/// <see cref="CliConnectionResolver.BuildServerConnection"/> must refuse up front with actionable guidance
/// instead of letting MSAL fail with 0xwindow_handle_required. This pins that refusal.
///
/// <para>Shares a collection with <see cref="EntraInteractiveAuthTests"/> because both read or flip the
/// process-wide registration state behind <see cref="EntraInteractiveAuth.IsSupported"/>; running them in
/// parallel would let that state change between this test's skip check and its assertion.</para>
/// </summary>
[Collection("EntraInteractiveAuth process-wide state")]
public class CliConnectionResolverTests
{
    [Fact]
    public void BuildServerConnection_RefusesInteractiveEntraWhereItCannotWork()
    {
        /* Only reachable on Windows: off Windows IsSupported is permanently true (browser auth works
           headless there) and the CLI correctly does not refuse. */
        Assert.SkipUnless(OperatingSystem.IsWindows(), "the refusal only exists where WAM does");

        /* Registration state is process-wide and one-way, and the registration tests may have run earlier
           in this process; reset the flag so this test observes the state the real CLI process is always
           in — nothing ever registered. The shared collection keeps the registration tests from running
           concurrently and re-flipping it mid-test. */
        EntraInteractiveAuth.ResetRegistrationForTests();

        var ex = Assert.Throws<InvalidOperationException>(() =>
            CliConnectionResolver.BuildServerConnection("srv", "entra", trustCert: false, new NoCredentials()));

        Assert.Contains("headless", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    /* Minimal stand-in: the resolver only asks whether a credential exists, and the entra refusal must fire
       before credentials ever matter. */
    private sealed class NoCredentials : ICredentialService
    {
        public bool SaveCredential(string serverId, string username, string password) => false;
        public (string Username, string Password)? GetCredential(string serverId) => null;
        public bool DeleteCredential(string serverId) => false;
        public bool CredentialExists(string serverId) => false;
        public bool UpdateCredential(string serverId, string username, string password) => false;
    }
}
