using System;
using System.IO;
using InstallerProgram = PlanViewer.Ssms.Installer.Program;

namespace PlanViewer.Ssms.Installer.Tests;

/// <summary>
/// The exit codes of <c>Program.VerifyOnly</c>, which the release workflow reads. The installer that the
/// tests load is the unsigned build output, so the installer-not-signed case is the real one here. The
/// release workflow treats every non-zero code the same way: it puts the unsigned pair back.
/// </summary>
public class VerifyOnlyTests : VsixTestBase
{
    [Fact]
    public void AnInstallerThatIsNotSignedFailsTheCheckEvenForAValidVsix()
    {
        // The control: the same VSIX passes the check against the certificate it was signed with.
        var vsix = NewVsix();
        CreateSignedVsix(vsix);
        Assert.Null(Check(vsix));

        Assert.Null(InstallerProgram.GetSigner(typeof(InstallerProgram).Assembly.Location));

        var output = new StringWriter();
        var original = Console.Out;
        int code;
        try
        {
            Console.SetOut(output);
            code = InstallerProgram.VerifyOnly(vsix);
        }
        finally
        {
            Console.SetOut(original);
        }

        Assert.Equal(InstallerProgram.VerifyInstallerNotSigned, code);
        Assert.NotEqual(0, code);
        Assert.Contains("not signed", output.ToString());
    }

    [Fact]
    public void AMissingVsixPathIsABadInputCode()
    {
        var original = Console.Out;
        int code;
        try
        {
            Console.SetOut(new StringWriter());
            code = InstallerProgram.VerifyOnly(NewVsix());
        }
        finally
        {
            Console.SetOut(original);
        }

        Assert.Equal(InstallerProgram.VerifyBadInput, code);
    }

    static void CreateSignedVsix(string path)
    {
        VsixFixture.CreateSigned(path, TestCertificates.Installer);
    }
}
