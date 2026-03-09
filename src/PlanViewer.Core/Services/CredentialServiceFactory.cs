using System.Runtime.InteropServices;
using PlanViewer.Core.Interfaces;

namespace PlanViewer.Core.Services;

public static class CredentialServiceFactory
{
    public static ICredentialService Create()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return new WindowsCredentialService();

        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            return new KeychainCredentialService();

        // Linux and other platforms: use in-memory storage (credentials not persisted across sessions)
        return new InMemoryCredentialService();
    }
}
