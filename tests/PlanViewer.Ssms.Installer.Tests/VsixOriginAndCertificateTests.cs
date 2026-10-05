using System.IO.Packaging;
using System.Linq;
using static PlanViewer.Ssms.Installer.Tests.VsixFixture;

namespace PlanViewer.Ssms.Installer.Tests;

/// <summary>
/// The parts that belong to the signature: the origin part, the signature part and the certificate
/// parts. The signature signs none of them, except when a signer chooses to sign the origin part.
/// The check accepts the origin part when it is signed or empty, and accepts a certificate part only
/// when it holds exactly the certificate of the installer. Relationships of the origin part and the
/// signature part may point only to entries that the check has accepted.
/// </summary>
public class VsixOriginAndCertificateTests : VsixTestBase
{
    const string ExtraCertificate = SignatureFolder + "certificate/extra.cer";
    const string ExtraCertificateRelationshipId = "rIdCertificate2";

    // ---- the origin part ----

    // The signing API makes the origin part itself, and leaves it empty and unsigned. These tests make
    // the origin part first, with the bytes and the signature that the test wants.
    string SignedWithOriginPart(byte[] originBytes, bool signOriginPart)
    {
        var vsix = NewVsix();
        CreateUnsigned(vsix, package =>
        {
            AddPart(package, OriginPart, OriginContentType, originBytes);
            package.CreateRelationship(PartUri(OriginPart), TargetMode.Internal, OriginRelationship, "rIdOrigin");
        });

        if (signOriginPart)
            Sign(vsix, TestCertificates.Installer, parts: package => ContentParts(package).Concat(new[] { PartUri(OriginPart) }).ToList());
        else
            Sign(vsix, TestCertificates.Installer);

        Assert.Equal(VerifyResult.Success, VerifySignature(vsix));
        Assert.Equal(signOriginPart, SignedEntryNames(vsix).Contains(EntryName(OriginPart)));
        return vsix;
    }

    [Fact]
    public void AnEmptyOriginPartThatIsNotSignedIsAccepted()
    {
        var vsix = SignedWithOriginPart(new byte[0], signOriginPart: false);

        Assert.Null(Check(vsix));
    }

    [Fact]
    public void AnOriginPartWithContentThatTheSignatureSignsIsAccepted()
    {
        var vsix = SignedWithOriginPart(Bytes("The signer put content in the origin part."), signOriginPart: true);

        Assert.Null(Check(vsix));
    }

    [Fact]
    public void AnOriginPartWithContentThatIsNotSignedIsRefused()
    {
        var vsix = SignedWithOriginPart(Bytes("Content that no signature covers."), signOriginPart: false);

        Assert.Equal(DoesNotCover(OriginPart), Check(vsix));
    }

    // ---- relationships of the origin part and the signature part ----

    [Fact]
    public void ARelationshipOfTheOriginPartThatPointsToASignedPartIsAccepted()
    {
        var vsix = NewVsix();
        CreateSigned(vsix, TestCertificates.Installer);
        AddRelationship(vsix, OriginPart, "rIdExtra", TestRelationship, License);

        Assert.Null(Check(vsix));
    }

    [Fact]
    public void ARelationshipOfTheSignaturePartThatPointsToASignedPartIsAccepted()
    {
        var vsix = NewVsix();
        CreateSigned(vsix, TestCertificates.Installer);
        EditPackage(vsix, package => SignaturePart(package).CreateRelationship(PartUri(License), TargetMode.Internal, TestRelationship, "rIdExtra"));

        Assert.Null(Check(vsix));
    }

    [Fact]
    public void ARelationshipOfTheOriginPartThatPointsToAnEntryNoSignatureCoversIsRefused()
    {
        var vsix = NewVsix();
        CreateSigned(vsix, TestCertificates.Installer);
        EditZip(vsix, zip => AddEntry(zip, "extra.txt", Bytes("Added after signing.")));
        AddRelationship(vsix, OriginPart, "rIdExtra", TestRelationship, "/extra.txt");

        // The entry is refused for having no cover, and so is the relationship that points to it.
        Assert.Equal(DoesNotCover("/extra.txt, relationship rIdExtra of " + OriginPart), Check(vsix));
    }

    [Fact]
    public void ARelationshipOfTheOriginPartThatPointsToAMissingEntryIsRefused()
    {
        var vsix = NewVsix();
        CreateSigned(vsix, TestCertificates.Installer);
        AddRelationship(vsix, OriginPart, "rIdExtra", TestRelationship, "/missing.txt");

        Assert.Equal(DoesNotCover("relationship rIdExtra of " + OriginPart), Check(vsix));
    }

    [Fact]
    public void ARelationshipOfTheOriginPartThatPointsOutsideThePackageIsRefused()
    {
        var vsix = NewVsix();
        CreateSigned(vsix, TestCertificates.Installer);
        AddRelationship(vsix, OriginPart, "rIdExtra", TestRelationship, "http://example.com/payload", TargetMode.External);

        Assert.Equal(DoesNotCover("relationship rIdExtra of " + OriginPart), Check(vsix));
    }

    [Fact]
    public void ARelationshipOfTheSignaturePartThatPointsOutsideThePackageIsRefused()
    {
        var vsix = NewVsix();
        CreateSigned(vsix, TestCertificates.Installer);
        EditPackage(vsix, package => SignaturePart(package).CreateRelationship(new System.Uri("http://example.com/payload"), TargetMode.External, TestRelationship, "rIdExtra"));

        // The name of the signature part holds a GUID that changes with every run.
        var reason = Check(vsix);
        Assert.NotNull(reason);
        Assert.StartsWith(DoesNotCover("relationship rIdExtra of " + SignatureFolder + "xml-signature/"), reason);
    }

    // ---- certificate parts ----

    void AddCertificatePart(string vsix, byte[] bytes, string contentType = CertificateContentType, bool linkedFromSignaturePart = true)
    {
        EditPackage(vsix, package =>
        {
            var signaturePart = SignaturePart(package);
            var certificatePart = AddPart(package, ExtraCertificate, contentType, bytes);
            if (linkedFromSignaturePart)
                signaturePart.CreateRelationship(certificatePart.Uri, TargetMode.Internal, CertificateRelationship, ExtraCertificateRelationshipId);
        });
    }

    // The reason for a certificate part that the check does not accept. It lists the part and then the
    // relationship of the signature part that links to it. The name of the signature part holds a GUID.
    static void AssertPartAndLinkRefused(string? reason)
    {
        Assert.NotNull(reason);
        Assert.StartsWith(
            DoesNotCover(ExtraCertificate + ", relationship " + ExtraCertificateRelationshipId + " of " + SignatureFolder + "xml-signature/"),
            reason);
    }

    [Fact]
    public void ACertificatePartWithExactlyTheCertificateOfTheInstallerIsAccepted()
    {
        var vsix = NewVsix();
        CreateSigned(vsix, TestCertificates.Installer);
        AddCertificatePart(vsix, TestCertificates.Installer.RawData);

        Assert.Contains(EntryName(ExtraCertificate), EntryNames(vsix));
        Assert.Null(Check(vsix));
    }

    [Fact]
    public void ACertificatePartNextToTheCertificatePartOfTheSignatureIsAcceptedWhenItHoldsTheSameCertificate()
    {
        var vsix = NewVsix();
        CreateSigned(vsix, TestCertificates.Installer, CertificateEmbeddingOption.InCertificatePart);
        AddCertificatePart(vsix, TestCertificates.Installer.RawData);

        Assert.Equal(2, EntryNames(vsix).Count(n => n.EndsWith(".cer", System.StringComparison.OrdinalIgnoreCase)));
        Assert.Null(Check(vsix));
    }

    [Fact]
    public void AnExtraCertificatePartWithAnotherCertificateIsRefused()
    {
        var vsix = NewVsix();
        CreateSigned(vsix, TestCertificates.Installer, CertificateEmbeddingOption.InCertificatePart);
        AddCertificatePart(vsix, TestCertificates.Other.RawData);

        AssertPartAndLinkRefused(Check(vsix));
    }

    [Fact]
    public void ACertificatePartWithOneByteAddedToTheCertificateIsRefused()
    {
        var vsix = NewVsix();
        CreateSigned(vsix, TestCertificates.Installer, CertificateEmbeddingOption.InCertificatePart);
        AddCertificatePart(vsix, TestCertificates.Installer.RawData.Concat(new byte[] { 0 }).ToArray());

        AssertPartAndLinkRefused(Check(vsix));
    }

    [Fact]
    public void ACertificatePartThatTheSignaturePartDoesNotLinkToIsRefused()
    {
        var vsix = NewVsix();
        CreateSigned(vsix, TestCertificates.Installer);
        AddCertificatePart(vsix, TestCertificates.Installer.RawData, linkedFromSignaturePart: false);

        // The bytes are the certificate of the installer, but no certificate relationship leads to the part.
        Assert.Equal(DoesNotCover(ExtraCertificate), Check(vsix));
    }

    [Fact]
    public void ACertificatePartWithAnotherContentTypeIsRefusedByOpcBeforeTheCoverageRule()
    {
        var vsix = NewVsix();
        CreateSigned(vsix, TestCertificates.Installer);
        AddCertificatePart(vsix, TestCertificates.Installer.RawData, contentType: "application/octet-stream");

        Assert.StartsWith(FailedToRead, Check(vsix));
    }
}
