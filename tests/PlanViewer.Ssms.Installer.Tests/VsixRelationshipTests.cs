using System.IO;
using System.IO.Packaging;
using System.Linq;
using static PlanViewer.Ssms.Installer.Tests.VsixFixture;

namespace PlanViewer.Ssms.Installer.Tests;

/// <summary>
/// Relationship parts, the <c>.rels</c> entries. A relationship part is covered when the signature signs
/// it whole or selects every relationship in it. The origin relationship of the package is the one
/// exception: a signer adds it after it signs, so it needs no cover. It is exempt only as the relationship
/// of the package itself, of the origin type, that points to the origin part. Every other relationship
/// with no cover is refused, whatever its type.
///
/// The parts of a VSIX have no relationships until a signer adds one, so each test that needs a
/// relationship adds it to the fixture first.
/// </summary>
public class VsixRelationshipTests : VsixTestBase
{
    const string LicenseRelationshipId = "rIdLicense";

    // The relationship of the manifest part to the license part, before any signing.
    static void AddLicenseRelationship(Package package)
    {
        package.GetPart(PartUri(Manifest)).CreateRelationship(PartUri(License), TargetMode.Internal, TestRelationship, LicenseRelationshipId);
    }

    static PackageRelationshipSelector SelectLicenseRelationshipById()
    {
        return new PackageRelationshipSelector(PartUri(Manifest), PackageRelationshipSelectorType.Id, LicenseRelationshipId);
    }

    string UnsignedWithLicenseRelationship()
    {
        var vsix = NewVsix();
        CreateUnsigned(vsix, AddLicenseRelationship);
        return vsix;
    }

    // ---- relationship parts of ordinary parts ----

    [Fact]
    public void ARelationshipPartThatNoSignatureCoversIsRefused()
    {
        var vsix = UnsignedWithLicenseRelationship();
        Sign(vsix, TestCertificates.Installer);

        Assert.DoesNotContain("_rels/extension.vsixmanifest.rels", SignedEntryNames(vsix));
        Assert.Equal(VerifyResult.Success, VerifySignature(vsix));
        Assert.Equal(DoesNotCover("relationship rIdLicense of /extension.vsixmanifest"), Check(vsix));
    }

    [Fact]
    public void ARelationshipPartThatTheSignatureSignsWholeIsAccepted()
    {
        var vsix = UnsignedWithLicenseRelationship();
        Sign(vsix, TestCertificates.Installer, parts: ContentAndRelationshipParts(Manifest));

        Assert.Contains("_rels/extension.vsixmanifest.rels", SignedEntryNames(vsix));
        Assert.Null(Check(vsix));
    }

    [Fact]
    public void ARelationshipThatASelectorPicksByIdIsAccepted()
    {
        var vsix = UnsignedWithLicenseRelationship();
        Sign(vsix, TestCertificates.Installer, selectors: new[] { SelectLicenseRelationshipById() });

        Assert.DoesNotContain("_rels/extension.vsixmanifest.rels", SignedEntryNames(vsix));
        Assert.Null(Check(vsix));
    }

    [Fact]
    public void ARelationshipThatASelectorPicksByTypeIsAccepted()
    {
        var vsix = UnsignedWithLicenseRelationship();
        Sign(vsix, TestCertificates.Installer, selectors: new[] { new PackageRelationshipSelector(PartUri(Manifest), PackageRelationshipSelectorType.Type, TestRelationship) });

        Assert.Null(Check(vsix));
    }

    [Fact]
    public void ARelationshipAddedAfterSigningIsRefusedWhenSelectorsSignTheOthers()
    {
        var vsix = UnsignedWithLicenseRelationship();
        Sign(vsix, TestCertificates.Installer, selectors: new[] { SelectLicenseRelationshipById() });
        Assert.Null(Check(vsix));

        // The selector picks rIdLicense only. The signature still verifies, and the new relationship has no cover.
        AddRelationship(vsix, Manifest, "rIdExtra", TestRelationship, License);

        Assert.Equal(VerifyResult.Success, VerifySignature(vsix));
        Assert.Equal(DoesNotCover("relationship rIdExtra of /extension.vsixmanifest"), Check(vsix));
    }

    [Fact]
    public void ARelationshipAddedToAPartThatHadNoneIsRefused()
    {
        var vsix = NewVsix();
        CreateSigned(vsix, TestCertificates.Installer);
        AddRelationship(vsix, Manifest, "rIdExtra", TestRelationship, License);

        // The relationship part is a new entry, and nothing covers it.
        Assert.Contains("_rels/extension.vsixmanifest.rels", EntryNames(vsix));
        Assert.Equal(DoesNotCover("relationship rIdExtra of /extension.vsixmanifest"), Check(vsix));
    }

    [Fact]
    public void ARelationshipAddedAfterSigningBreaksTheSignatureOfAWholeRelationshipPart()
    {
        var vsix = UnsignedWithLicenseRelationship();
        Sign(vsix, TestCertificates.Installer, parts: ContentAndRelationshipParts(Manifest));
        AddRelationship(vsix, Manifest, "rIdExtra", TestRelationship, License);

        Assert.Equal(NotValid(VerifyResult.InvalidSignature), Check(vsix));
    }

    [Fact]
    public void ARelationshipOfASelectedTypeAddedAfterSigningBreaksTheSignature()
    {
        var vsix = UnsignedWithLicenseRelationship();
        Sign(vsix, TestCertificates.Installer, selectors: new[] { new PackageRelationshipSelector(PartUri(Manifest), PackageRelationshipSelectorType.Type, TestRelationship) });
        AddRelationship(vsix, Manifest, "rIdExtra", TestRelationship, License);

        // A selector by type signs every relationship of that type, and now there is one more.
        Assert.Equal(NotValid(VerifyResult.InvalidSignature), Check(vsix));
    }

    [Fact]
    public void AnExternalRelationshipAddedAfterSigningIsRefused()
    {
        var vsix = NewVsix();
        CreateSigned(vsix, TestCertificates.Installer);
        AddRelationship(vsix, Manifest, "rIdExternal", TestRelationship, "http://example.com/payload", TargetMode.External);

        Assert.Equal(DoesNotCover("relationship rIdExternal of /extension.vsixmanifest"), Check(vsix));
    }

    [Fact]
    public void ACertificateRelationshipOnAnOrdinaryPartIsRefused()
    {
        var vsix = NewVsix();
        CreateSigned(vsix, TestCertificates.Installer);
        AddRelationship(vsix, Manifest, "rIdCertificate", CertificateRelationship, License);

        // Only the relationships of the origin part and the signature part are exempt, whatever their type.
        Assert.Equal(DoesNotCover("relationship rIdCertificate of /extension.vsixmanifest"), Check(vsix));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ARelationshipPartForAPartThatDoesNotExistIsRefused(bool withRelationship)
    {
        var vsix = NewVsix();
        CreateSigned(vsix, TestCertificates.Installer);
        var relationships = withRelationship
            ? "<Relationship Id=\"rId1\" Type=\"" + TestRelationship + "\" Target=\"/LICENSE.txt\" />"
            : "";
        EditZip(vsix, zip => AddEntry(zip, "_rels/missing.bin.rels",
            Bytes("<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" + relationships + "</Relationships>")));

        Assert.Equal(DoesNotCover("/_rels/missing.bin.rels"), Check(vsix));
    }

    // ---- the origin relationship of the package ----

    [Fact]
    public void TheOriginRelationshipOfThePackageIsAcceptedWithNoSignatureOverIt()
    {
        var vsix = NewVsix();
        CreateSigned(vsix, TestCertificates.Installer);

        // The signing API adds the origin relationship to the package after it signs. The package
        // relationship part therefore exists, holds the origin relationship, and is not signed.
        Assert.Contains("_rels/.rels", EntryNames(vsix));
        Assert.DoesNotContain("_rels/.rels", SignedEntryNames(vsix));
        using (var package = Package.Open(vsix, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Assert.Single(package.GetRelationships());
            Assert.Single(package.GetRelationshipsByType(OriginRelationship));
        }

        Assert.Null(Check(vsix));
    }

    [Fact]
    public void APackageRelationshipNextToTheOriginRelationshipIsRefusedWhenNothingCoversIt()
    {
        var vsix = NewVsix();
        CreateUnsigned(vsix, package => package.CreateRelationship(PartUri(Manifest), TargetMode.Internal, TestRelationship, "rIdRoot"));
        Sign(vsix, TestCertificates.Installer);

        Assert.Equal(DoesNotCover("relationship rIdRoot of /"), Check(vsix));
    }

    [Fact]
    public void APackageRelationshipThatASelectorPicksIsAccepted()
    {
        var vsix = NewVsix();
        CreateUnsigned(vsix, package => package.CreateRelationship(PartUri(Manifest), TargetMode.Internal, TestRelationship, "rIdRoot"));
        Sign(vsix, TestCertificates.Installer, selectors: new[] { new PackageRelationshipSelector(PartUri("/"), PackageRelationshipSelectorType.Id, "rIdRoot") });

        // The selector picks rIdRoot. The origin relationship next to it needs no selector.
        Assert.Null(Check(vsix));
    }

    [Fact]
    public void ThePackageRelationshipPartThatTheSignatureSignsWholeIsAccepted()
    {
        var vsix = NewVsix();
        CreateUnsigned(vsix, package => package.CreateRelationship(PartUri(Manifest), TargetMode.Internal, TestRelationship, "rIdRoot"));
        Sign(vsix, TestCertificates.Installer, parts: ContentAndRelationshipParts("/"));

        Assert.Contains("_rels/.rels", SignedEntryNames(vsix));
        Assert.Null(Check(vsix));
    }

    [Fact]
    public void APackageRelationshipAddedAfterSigningIsRefused()
    {
        var vsix = NewVsix();
        CreateSigned(vsix, TestCertificates.Installer);
        AddRelationship(vsix, "/", "rIdRoot", TestRelationship, Manifest);

        Assert.Equal(DoesNotCover("relationship rIdRoot of /"), Check(vsix));
    }

    [Fact]
    public void APackageRelationshipOfAnotherSignatureTypeIsRefused()
    {
        var vsix = NewVsix();
        CreateSigned(vsix, TestCertificates.Installer);

        // A relationship type under the signature namespace is not the origin type, so it is not exempt.
        AddRelationship(vsix, "/", "rIdRoot", SignatureRelationshipPrefix + "other", Manifest);

        Assert.Equal(DoesNotCover("relationship rIdRoot of /"), Check(vsix));
    }

    [Fact]
    public void ASecondOriginRelationshipThatPointsToAnotherPartIsRefused()
    {
        var vsix = NewVsix();
        CreateSigned(vsix, TestCertificates.Installer);
        AddRelationship(vsix, "/", "rIdOrigin2", OriginRelationship, Manifest);

        // The exemption is for the relationship that points to the origin part, and this one does not.
        Assert.Equal(DoesNotCover("relationship rIdOrigin2 of /"), Check(vsix));
    }

    [Fact]
    public void ASecondOriginRelationshipThatPointsToTheOriginPartIsRefusedByOpc()
    {
        var vsix = NewVsix();
        CreateSigned(vsix, TestCertificates.Installer);
        AddRelationship(vsix, "/", "rIdOrigin2", OriginRelationship, OriginPart);

        Assert.StartsWith(FailedToRead, Check(vsix));
    }

    [Fact]
    public void AnOriginRelationshipOnAnOrdinaryPartIsRefused()
    {
        var vsix = NewVsix();
        CreateSigned(vsix, TestCertificates.Installer);
        AddRelationship(vsix, Manifest, "rIdOrigin", OriginRelationship, OriginPart);

        // The exemption is for the relationship of the package, not for a relationship of a part.
        Assert.Equal(DoesNotCover("relationship rIdOrigin of /extension.vsixmanifest"), Check(vsix));
    }
}
