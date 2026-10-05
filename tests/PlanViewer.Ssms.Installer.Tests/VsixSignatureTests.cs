using System;
using System.IO;
using System.IO.Compression;
using System.IO.Packaging;
using System.Security.Cryptography.X509Certificates;
using static PlanViewer.Ssms.Installer.Tests.VsixFixture;
using InstallerProgram = PlanViewer.Ssms.Installer.Program;

namespace PlanViewer.Ssms.Installer.Tests;

/// <summary>
/// The first steps of <c>Program.CheckVsix</c>, which are about the signature itself: it passes a VSIX
/// with exactly one valid signature, made with the certificate of the installer, and it refuses a file
/// with no signature, with two, with the certificate of another signer, or with a part that no longer
/// matches its signature. The steps run in that order and stop at the first failure.
///
/// The VSIX files are built by <see cref="VsixFixture"/> at test time and signed with certificates that
/// <see cref="TestCertificates"/> makes in memory.
/// </summary>
public class VsixSignatureTests : VsixTestBase
{
    [Fact]
    public void AVsixSignedWithTheInstallerCertificateThatCoversEveryPartIsAccepted()
    {
        var vsix = NewVsix();
        CreateSigned(vsix, TestCertificates.Installer);

        // A control is worth something only when the signature really covers every content part.
        var signed = SignedEntryNames(vsix);
        foreach (var name in DefaultPartNames)
            Assert.Contains(EntryName(name), signed);
        Assert.Equal(VerifyResult.Success, VerifySignature(vsix));

        Assert.Null(Check(vsix));
    }

    [Fact]
    public void TheCertificateMayBeInItsOwnPartInsteadOfTheSignaturePart()
    {
        var vsix = NewVsix();
        CreateSigned(vsix, TestCertificates.Installer, CertificateEmbeddingOption.InCertificatePart);

        Assert.Single(EntryNames(vsix), n => n.EndsWith(".cer", StringComparison.OrdinalIgnoreCase));
        Assert.Null(Check(vsix));
    }

    [Fact]
    public void AnUnsignedVsixIsRefused()
    {
        var vsix = NewVsix();
        CreateUnsigned(vsix);

        Assert.Equal(NotSigned, Check(vsix));
    }

    [Fact]
    public void AVsixSignedWithAnotherCertificateIsRefused()
    {
        var vsix = NewVsix();
        CreateSigned(vsix, TestCertificates.Other);

        Assert.Equal(DifferentCertificate, Check(vsix));

        // The file is sound. It is refused only because the installer has another certificate.
        Assert.Equal(VerifyResult.Success, VerifySignature(vsix));
        Assert.Null(Check(vsix, TestCertificates.Other));
    }

    [Fact]
    public void TheWholeCertificateMustMatchNotOnlyItsThumbprint()
    {
        var vsix = NewVsix();
        CreateSigned(vsix, TestCertificates.Installer);

        // A certificate with the thumbprint of the signer and the bytes of another certificate.
        var twin = new ThumbprintTwin(TestCertificates.Other, TestCertificates.Installer);
        Assert.Equal(TestCertificates.Installer.GetCertHashString(), twin.GetCertHashString());

        Assert.Equal(DifferentCertificate, InstallerProgram.CheckVsix(vsix, twin));
        Assert.Null(Check(vsix));
    }

    [Fact]
    public void TheCertificateIsComparedBeforeTheSignatureIsVerified()
    {
        var vsix = NewVsix();
        CreateSigned(vsix, TestCertificates.Other);
        EditZip(vsix, zip => ReplaceEntry(zip, EntryName(Assembly), Bytes("changed")));

        // The signature is broken as well, but the certificate of the signer is not the certificate
        // of the installer, and that is the reason that comes back.
        Assert.Equal(VerifyResult.InvalidSignature, VerifySignature(vsix));
        Assert.Equal(DifferentCertificate, Check(vsix));
    }

    [Fact]
    public void ASignatureThatHoldsNoCertificateIsRefused()
    {
        var vsix = NewVsix();
        CreateSigned(vsix, TestCertificates.Installer, CertificateEmbeddingOption.NotEmbedded);

        Assert.Equal(NotValid(VerifyResult.CertificateRequired), Check(vsix));
    }

    [Fact]
    public void AFileWithTwoSignaturesIsRefused()
    {
        var vsix = NewVsix();
        CreateSigned(vsix, TestCertificates.Installer);
        Sign(vsix, TestCertificates.Other);

        Assert.Equal(MoreThanOneSignature, Check(vsix));
    }

    [Fact]
    public void TwoSignaturesFromTheInstallerCertificateAreRefusedToo()
    {
        var vsix = NewVsix();
        CreateSigned(vsix, TestCertificates.Installer);
        Sign(vsix, TestCertificates.Installer);

        Assert.Equal(MoreThanOneSignature, Check(vsix));
    }

    [Fact]
    public void APartThatChangedAfterSigningIsRefused()
    {
        var vsix = NewVsix();
        CreateSigned(vsix, TestCertificates.Installer);
        EditZip(vsix, zip => ReplaceEntry(zip, EntryName(Assembly), Bytes("MZ and then something else")));

        Assert.Equal(NotValid(VerifyResult.InvalidSignature), Check(vsix));
    }

    [Fact]
    public void APartThatWasRemovedAfterSigningIsRefused()
    {
        var vsix = NewVsix();
        CreateSigned(vsix, TestCertificates.Installer);
        EditZip(vsix, zip => zip.GetEntry(EntryName(Assembly))!.Delete());

        Assert.Equal(NotValid(VerifyResult.ReferenceNotFound), Check(vsix));
    }

    [Fact]
    public void ANewContentTypeForASignedPartIsRefused()
    {
        var vsix = NewVsix();
        CreateSigned(vsix, TestCertificates.Installer);

        // [Content_Types].xml is not signed, but the signature covers the content type of each part it signs.
        EditZip(vsix, zip => ReplaceInEntry(zip, ContentTypesEntry, "ContentType=\"text/xml\"", "ContentType=\"text/evil\""));

        Assert.Equal(NotValid(VerifyResult.InvalidSignature), Check(vsix));
    }

    [Fact]
    public void ASignatureValueThatChangedIsRefused()
    {
        var vsix = NewVsix();
        CreateSigned(vsix, TestCertificates.Installer);
        EditZip(vsix, FlipSignatureValue);

        Assert.Equal(NotValid(VerifyResult.InvalidSignature), Check(vsix));
    }

    [Fact]
    public void ACertificatePartThatWasReplacedWithAnotherCertificateMakesTheSignerDifferent()
    {
        var vsix = NewVsix();
        CreateSigned(vsix, TestCertificates.Installer, CertificateEmbeddingOption.InCertificatePart);
        EditZip(vsix, zip => ReplaceEntry(zip, EntryWithExtension(zip, ".cer"), TestCertificates.Other.RawData));

        Assert.Equal(DifferentCertificate, Check(vsix));
    }

    [Fact]
    public void AFileThatIsNotAZipIsRefused()
    {
        var vsix = NewVsix();
        File.WriteAllText(vsix, "This is not a ZIP file.");

        Assert.StartsWith(FailedToRead, Check(vsix));
    }

    [Fact]
    public void AMissingFileIsRefused()
    {
        Assert.StartsWith(FailedToRead, Check(NewVsix()));
    }

    [Fact]
    public void AZipFileThatIsNotAPackageHasNoSignatureAndIsRefused()
    {
        var vsix = NewVsix();
        using (var stream = new FileStream(vsix, FileMode.Create))
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
            AddEntry(zip, "readme.txt", Bytes("A ZIP file with no content types."));

        Assert.Equal(NotSigned, Check(vsix));
    }

    /// <summary>
    /// A certificate that reports the thumbprint of one certificate and holds the bytes of another. No
    /// real certificate is like this, because the thumbprint is a hash of the bytes. The twin stands for
    /// a check that compares thumbprints only, and it shows that CheckVsix compares the whole certificate.
    /// </summary>
    sealed class ThumbprintTwin : X509Certificate
    {
        readonly X509Certificate thumbprintOf;

        public ThumbprintTwin(X509Certificate bytesOf, X509Certificate thumbprintOf)
            : base(bytesOf.GetRawCertData())
        {
            this.thumbprintOf = thumbprintOf;
        }

        public override byte[] GetCertHash() => thumbprintOf.GetCertHash();

        public override string GetCertHashString() => thumbprintOf.GetCertHashString();
    }
}
