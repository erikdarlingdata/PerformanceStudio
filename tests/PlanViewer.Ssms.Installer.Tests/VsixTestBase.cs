using System;
using System.IO;
using System.IO.Packaging;
using System.Security.Cryptography.X509Certificates;
using Xunit.Sdk;
using Xunit.v3;
using InstallerProgram = PlanViewer.Ssms.Installer.Program;

// The tests are small and quick. They share only the two certificates, so turning parallelism
// off costs nothing and removes any doubt about signing from several threads at once.
[assembly: Parallelization(Mode = ParallelMode.None)]

namespace PlanViewer.Ssms.Installer.Tests;

/// <summary>
/// The base of the tests of <c>Program.CheckVsix</c>. Each test gets a private temp folder for its VSIX
/// files, and the folder is deleted when the test ends. It also holds the reasons that CheckVsix
/// returns, so that a test states the reason it expects.
/// </summary>
public abstract class VsixTestBase : IDisposable
{
    // The reasons of CheckVsix. It returns null when it accepts the file.
    protected const string NotSigned = "the file is not signed";
    protected const string MoreThanOneSignature = "the file has more than one signature";
    protected const string DifferentCertificate = "the file is signed with a different certificate than this installer";

    // The reason starts like this when System.IO.Packaging or the ZIP reader refuses the file, and the
    // rest of it is the message of the .NET exception. That message is in the language of Windows, so
    // the tests match only this start.
    protected const string FailedToRead = "the installer failed to read the file (";

    protected static string NotValid(VerifyResult result) => $"the signature is not valid ({result})";

    protected static string DoesNotCover(string what) => "the signature does not cover " + what;

    readonly string folder = Path.Combine(Path.GetTempPath(), "PlanViewerSsmsInstallerTests-" + Guid.NewGuid().ToString("N"));

    protected VsixTestBase()
    {
        Directory.CreateDirectory(folder);
    }

    /// <summary>The path for a new VSIX file. It does not exist yet.</summary>
    protected string NewVsix() => Path.Combine(folder, Guid.NewGuid().ToString("N") + ".vsix");

    /// <summary>
    /// Runs the check of the installer on the file. The installer certificate is the one the tests
    /// treat as the certificate of the signed installer, unless the test passes another. It is passed
    /// as the installer holds it: a plain X509Certificate with no private key.
    /// </summary>
    protected static string? Check(string vsix, X509Certificate2? installerCertificate = null)
    {
        return InstallerProgram.CheckVsix(vsix, TestCertificates.AsInstallerCertificate(installerCertificate ?? TestCertificates.Installer));
    }

    /// <summary>The verdict of System.IO.Packaging on the signature alone, with none of the coverage rules of the installer.</summary>
    protected static VerifyResult VerifySignature(string vsix)
    {
        using (var package = Package.Open(vsix, FileMode.Open, FileAccess.Read, FileShare.Read))
            return new PackageDigitalSignatureManager(package).VerifySignatures(false);
    }

    public void Dispose()
    {
        try { Directory.Delete(folder, true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
