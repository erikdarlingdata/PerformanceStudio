using PlanViewer.Core.Interfaces;

namespace PlanViewer.Core.Services;

public static class CredentialServiceFactory
{
    private static ICredentialService? _testHostService;

    /// <summary>
    /// Sends every credential read and write to one in-memory store for the test host.
    ///
    /// <para>Without this, a test that reaches any save path touching credentials operates on the
    /// developer's real Windows Credential Manager — reading their stored passwords, and deleting
    /// one outright if the code decides a blank field means "remove it". The settings JSON is
    /// already redirected for the same reason; this closes the other half, so test safety here is
    /// a property of the harness rather than of nobody having written that test yet.</para>
    ///
    /// <para>One shared instance, so a save and a later load inside a run agree with each other.
    /// Never called by the app: when it is not called, <see cref="Create"/> resolves by platform
    /// exactly as before.</para>
    /// </summary>
    internal static void UseInMemoryForTestHost() => _testHostService = new InMemoryCredentialService();

    public static ICredentialService Create()
    {
        if (_testHostService != null)
            return _testHostService;

        // CA1416: the underlying CredentialManager API declares "windows5.1.2600" (XP+);
        // .NET 10 won't run on anything below Windows 10, so OperatingSystem.IsWindows() is sufficient.
#pragma warning disable CA1416
        if (OperatingSystem.IsWindows())
            return new WindowsCredentialService();
#pragma warning restore CA1416

        if (OperatingSystem.IsMacOS())
            return new KeychainCredentialService();

        // Linux and other platforms: use in-memory storage (credentials not persisted across sessions)
        return new InMemoryCredentialService();
    }
}
