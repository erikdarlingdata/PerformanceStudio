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

        throw new PlatformNotSupportedException(
            "Credential storage is not yet supported on this platform. " +
            "Windows and macOS are supported.");
    }
}
