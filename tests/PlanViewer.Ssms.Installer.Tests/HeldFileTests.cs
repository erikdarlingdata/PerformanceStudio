using System;
using System.IO;
using InstallerProgram = PlanViewer.Ssms.Installer.Program;

namespace PlanViewer.Ssms.Installer.Tests;

/// <summary>
/// The installer holds the file that gets installed until the last VSIXInstaller has exited. While it
/// holds the file, no other process can change it, delete it, or rename it or its folder.
/// </summary>
public class HeldFileTests : IDisposable
{
    readonly string folder = Path.Combine(Path.GetTempPath(), "PlanViewerSsmsInstallerTests-" + Guid.NewGuid().ToString("N"));
    readonly string file;

    public HeldFileTests()
    {
        Directory.CreateDirectory(folder);
        file = Path.Combine(folder, "PlanViewer.Ssms.vsix");
        File.WriteAllText(file, "vsix");
    }

    public void Dispose()
    {
        try { Directory.Delete(folder, true); } catch { }
    }

    [Fact]
    public void AHeldFileCannotBeOpenedForWriting()
    {
        using (InstallerProgram.HoldFile(file))
        {
            Assert.Throws<IOException>(() => File.OpenWrite(file).Dispose());
            Assert.Throws<IOException>(() => File.WriteAllText(file, "other"));
        }
    }

    [Fact]
    public void AHeldFileCannotBeDeletedOrRenamed()
    {
        using (InstallerProgram.HoldFile(file))
        {
            Assert.ThrowsAny<IOException>(() => File.Delete(file));
            Assert.Throws<IOException>(() => File.Move(file, file + ".old"));
            Assert.True(File.Exists(file));
        }
    }

    [Fact]
    public void TheFolderOfAHeldFileCannotBeRenamedOrDeleted()
    {
        using (InstallerProgram.HoldFile(file))
        {
            Assert.Throws<IOException>(() => Directory.Move(folder, folder + "-moved"));
            Assert.ThrowsAny<IOException>(() => Directory.Delete(folder, true));
            Assert.True(File.Exists(file));
        }
    }

    [Fact]
    public void AHeldFileCanStillBeReadByAnotherProcess()
    {
        // VSIXInstaller opens the file for reading while the installer holds it.
        using (InstallerProgram.HoldFile(file))
        using (var reader = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Assert.Equal(4, reader.Length);
        }
    }

    [Fact]
    public void ACheckOfAHeldFileStillWorks()
    {
        // CheckVsix reads the file while the installer holds it. The file is not a VSIX, so the check
        // refuses it, but it must refuse it for that reason and not because the file is locked.
        using (InstallerProgram.HoldFile(file))
        {
            var reason = InstallerProgram.CheckVsix(file, TestCertificates.AsInstallerCertificate(TestCertificates.Installer));
            Assert.NotNull(reason);
            Assert.StartsWith("the installer failed to read the file (", reason);
            Assert.DoesNotContain("being used by another process", reason);
        }
    }

    [Fact]
    public void ReleasingTheHoldFreesTheFileAndItsFolder()
    {
        InstallerProgram.HoldFile(file).Dispose();
        Directory.Delete(folder, true);
        Assert.False(Directory.Exists(folder));
    }
}
