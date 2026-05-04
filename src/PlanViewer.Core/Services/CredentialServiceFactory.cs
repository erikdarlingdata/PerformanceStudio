using PlanViewer.Core.Interfaces;

namespace PlanViewer.Core.Services;

public static class CredentialServiceFactory
{
    public static ICredentialService Create()
    {
        // CA1416: the underlying CredentialManager API declares "windows5.1.2600" (XP+);
        // .NET 8 won't run on anything below Windows 10, so OperatingSystem.IsWindows() is sufficient.
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
