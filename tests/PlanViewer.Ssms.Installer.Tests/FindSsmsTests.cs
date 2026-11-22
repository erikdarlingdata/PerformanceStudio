using System;
using System.Collections.Generic;
using InstallerProgram = PlanViewer.Ssms.Installer.Program;

namespace PlanViewer.Ssms.Installer.Tests;

/// <summary>
/// The installer finds each SSMS version by its VSIXInstaller path and runs the VSIXInstaller of the newest one.
/// SSMS 21 installs under Release\, the same as SSMS 22.
/// </summary>
public class FindSsmsTests
{
    const string Ssms22 = @"C:\Program Files\Microsoft SQL Server Management Studio 22\Release\Common7\IDE\VSIXInstaller.exe";
    const string Ssms21 = @"C:\Program Files\Microsoft SQL Server Management Studio 21\Release\Common7\IDE\VSIXInstaller.exe";
    const string Ssms21WithoutRelease = @"C:\Program Files\Microsoft SQL Server Management Studio 21\Common7\IDE\VSIXInstaller.exe";

    static (string Label, string VsixInstallerPath)[] Find(params string[] existing)
    {
        var paths = new HashSet<string>(existing, StringComparer.OrdinalIgnoreCase);
        return InstallerProgram.FindSsms(paths.Contains);
    }

    [Fact]
    public void BothVersionsAreFoundNewestFirst()
    {
        var found = Find(Ssms21, Ssms22);

        Assert.Equal(new[] { ("SSMS 22", Ssms22), ("SSMS 21", Ssms21) }, found);
    }

    [Fact]
    public void Ssms21AloneIsFoundUnderRelease()
    {
        var found = Find(Ssms21);

        Assert.Equal(new[] { ("SSMS 21", Ssms21) }, found);
    }

    [Fact]
    public void Ssms21WithoutReleaseIsStillFound()
    {
        var found = Find(Ssms21WithoutRelease);

        Assert.Equal(new[] { ("SSMS 21", Ssms21WithoutRelease) }, found);
    }

    [Fact]
    public void Ssms21IsListedOnceWhenBothOfItsPathsExist()
    {
        var found = Find(Ssms21WithoutRelease, Ssms21);

        Assert.Equal(new[] { ("SSMS 21", Ssms21) }, found);
    }

    [Fact]
    public void NothingIsFoundWhenNoVsixInstallerExists()
    {
        Assert.Empty(Find());
    }
}
