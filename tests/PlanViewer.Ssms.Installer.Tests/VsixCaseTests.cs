using System.IO.Packaging;
using static PlanViewer.Ssms.Installer.Tests.VsixFixture;

namespace PlanViewer.Ssms.Installer.Tests;

/// <summary>
/// ZIP entry names that differ only in case. The check compares names exactly, so such names are
/// different names. Windows treats them as one file when it unpacks the VSIX, so a name in another
/// case must never count as covered. The check also refuses a second entry with the same name in any
/// case. OPC refuses some of these files by itself, before the coverage rule runs. Then the reason
/// says that the installer failed to read the file.
/// </summary>
public class VsixCaseTests : VsixTestBase
{
    const string EmptyContentTypes = "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\" />";

    [Fact]
    public void TwoEntriesThatDifferOnlyInCaseAreBothRefusedAndTheSecondIsNamedAsARepeat()
    {
        var vsix = NewVsix();
        CreateSigned(vsix, TestCertificates.Installer);
        EditZip(vsix, zip =>
        {
            AddEntry(zip, "data.xyz", Bytes("The first."));
            AddEntry(zip, "DATA.XYZ", Bytes("The second."));
        });

        // Neither entry is covered. The second one is also a second entry with the name of the first.
        Assert.Equal(
            DoesNotCover("/DATA.XYZ (a second entry with the same name), /data.xyz"),
            Check(vsix));
    }

    [Fact]
    public void ASecondContentTypesEntryInAnotherCaseIsRefusedAsASecondEntryWithTheSameName()
    {
        var vsix = NewVsix();
        CreateSigned(vsix, TestCertificates.Installer);
        EditZip(vsix, zip => AddEntry(zip, "[CONTENT_TYPES].XML", Bytes(EmptyContentTypes)));

        // The first [Content_Types].xml is one of the entries that need no signature. The second is not.
        Assert.Equal(DoesNotCover("/[CONTENT_TYPES].XML (a second entry with the same name)"), Check(vsix));
    }

    [Fact]
    public void ASecondEntryWithTheNameOfASignedPartInAnotherCaseIsRefusedByOpcBeforeTheCoverageRule()
    {
        var vsix = NewVsix();
        CreateSigned(vsix, TestCertificates.Installer);
        EditZip(vsix, zip => AddEntry(zip, "planviewer.ssms.DLL", Bytes("MZ another assembly.")));

        Assert.StartsWith(FailedToRead, Check(vsix));
    }

    [Fact]
    public void ASecondEntryWithTheSameNameAsASignedPartIsRefusedByOpcBeforeTheCoverageRule()
    {
        var vsix = NewVsix();
        CreateSigned(vsix, TestCertificates.Installer);
        EditZip(vsix, zip => AddEntry(zip, EntryName(Assembly), Bytes("MZ another assembly.")));

        Assert.StartsWith(FailedToRead, Check(vsix));
    }

    [Fact]
    public void ASignedPartRenamedToAnotherCaseIsRefused()
    {
        var vsix = NewVsix();
        CreateSigned(vsix, TestCertificates.Installer);
        EditZip(vsix, zip => RenameEntry(zip, EntryName(Assembly), "planviewer.ssms.dll"));

        // OPC compares part names without regard to case, so the signature still verifies.
        // Only the exact comparison of the check sees that the entry is not the signed one.
        Assert.Equal(VerifyResult.Success, VerifySignature(vsix));
        Assert.Equal(DoesNotCover("/planviewer.ssms.dll"), Check(vsix));
    }

    [Fact]
    public void TheContentTypesEntryRenamedToAnotherCaseIsRefused()
    {
        var vsix = NewVsix();
        CreateSigned(vsix, TestCertificates.Installer);
        EditZip(vsix, zip => RenameEntry(zip, ContentTypesEntry, "[content_types].xml"));

        Assert.Equal(VerifyResult.Success, VerifySignature(vsix));
        Assert.Equal(DoesNotCover("/[content_types].xml"), Check(vsix));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TheRelationshipPartOfASignedPartRenamedToAnotherCaseIsRefused(bool signedWhole)
    {
        var vsix = NewVsix();
        CreateUnsigned(vsix, package => package.GetPart(PartUri(Manifest)).CreateRelationship(PartUri(License), TargetMode.Internal, TestRelationship, "rIdLicense"));
        if (signedWhole)
            Sign(vsix, TestCertificates.Installer, parts: ContentAndRelationshipParts(Manifest));
        else
            Sign(vsix, TestCertificates.Installer, selectors: new[] { new PackageRelationshipSelector(PartUri(Manifest), PackageRelationshipSelectorType.Id, "rIdLicense") });

        // Before the rename the file passes, so the rename is the only reason to refuse it.
        Assert.Null(Check(vsix));
        EditZip(vsix, zip => RenameEntry(zip, "_rels/extension.vsixmanifest.rels", "_rels/Extension.vsixmanifest.rels"));

        Assert.Equal(DoesNotCover("/_rels/Extension.vsixmanifest.rels"), Check(vsix));
    }

    [Fact]
    public void ThePackageRelationshipPartRenamedToAnotherCaseIsRefused()
    {
        var vsix = NewVsix();
        CreateSigned(vsix, TestCertificates.Installer);
        EditZip(vsix, zip => RenameEntry(zip, "_rels/.rels", "_RELS/.rels"));

        Assert.Equal(DoesNotCover("/_RELS/.rels"), Check(vsix));
    }

    [Fact]
    public void TheOriginPartRenamedToAnotherCaseIsRefused()
    {
        var vsix = NewVsix();
        CreateSigned(vsix, TestCertificates.Installer);
        EditZip(vsix, zip => RenameEntry(zip, EntryName(OriginPart), "package/services/digital-signature/ORIGIN.psdsor"));

        // The reason lists the entry and then a relationship, whose id OPC picks at random.
        var reason = Check(vsix);
        Assert.NotNull(reason);
        Assert.StartsWith(DoesNotCover("/package/services/digital-signature/ORIGIN.psdsor"), reason);
    }

    [Fact]
    public void TheRelationshipPartOfTheOriginPartRenamedToAnotherCaseIsRefused()
    {
        var vsix = NewVsix();
        CreateSigned(vsix, TestCertificates.Installer);
        EditZip(vsix, zip => RenameEntry(zip, "package/services/digital-signature/_rels/origin.psdsor.rels", "package/services/digital-signature/_rels/ORIGIN.psdsor.rels"));

        Assert.Equal(DoesNotCover("/package/services/digital-signature/_rels/ORIGIN.psdsor.rels"), Check(vsix));
    }

    [Fact]
    public void TheSignaturePartRenamedToAnotherCaseIsRefused()
    {
        var vsix = NewVsix();
        CreateSigned(vsix, TestCertificates.Installer);
        EditZip(vsix, zip =>
        {
            var name = EntryWithExtension(zip, ".psdsxs");
            RenameEntry(zip, name, name.Replace("xml-signature/", "XML-SIGNATURE/"));
        });

        // The relationship of the origin part now points to an entry that has another name.
        var reason = Check(vsix);
        Assert.NotNull(reason);
        Assert.StartsWith(DoesNotCover("relationship R"), reason);
        Assert.EndsWith(" of " + OriginPart, reason);
    }

    [Fact]
    public void TheCertificatePartRenamedToAnotherCaseIsRefused()
    {
        var vsix = NewVsix();
        CreateSigned(vsix, TestCertificates.Installer, CertificateEmbeddingOption.InCertificatePart);
        EditZip(vsix, zip =>
        {
            var name = EntryWithExtension(zip, ".cer");
            RenameEntry(zip, name, name.Replace("certificate/", "CERTIFICATE/"));
        });

        // The name of the certificate part comes from the thumbprint, which changes with every run.
        var reason = Check(vsix);
        Assert.NotNull(reason);
        Assert.StartsWith(DoesNotCover("/package/services/digital-signature/CERTIFICATE/"), reason);
        Assert.Contains(".cer, relationship R", reason);
    }
}
