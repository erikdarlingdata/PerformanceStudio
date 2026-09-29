using System.IO.Packaging;
using System.Linq;
using static PlanViewer.Ssms.Installer.Tests.VsixFixture;

namespace PlanViewer.Ssms.Installer.Tests;

/// <summary>
/// The last step of <c>Program.CheckVsix</c>: every entry of the ZIP file must be covered by the signature
/// or be one of the few entries that a signature cannot cover. The list of entries comes from the raw
/// ZIP file, not from the parts that OPC finds, so an entry that OPC does not list as a part is caught
/// too. In each test the signature still verifies, so it is the entry rule that refuses the file.
/// </summary>
public class VsixEntryTests : VsixTestBase
{
    [Fact]
    public void AnExtraPartThatTheSignatureDoesNotCoverIsRefused()
    {
        var vsix = NewVsix();
        CreateSigned(vsix, TestCertificates.Installer);
        EditZip(vsix, zip => AddEntry(zip, "extra.txt", Bytes("Added after signing.")));

        // The extension txt has a content type, so OPC lists the entry as a part.
        Assert.Contains("/extra.txt", PartNames(vsix));
        Assert.Equal(VerifyResult.Success, VerifySignature(vsix));

        Assert.Equal(DoesNotCover("/extra.txt"), Check(vsix));
    }

    [Fact]
    public void AnExtraEntryThatOpcDoesNotListAsAPartIsRefused()
    {
        var vsix = NewVsix();
        CreateSigned(vsix, TestCertificates.Installer);
        EditZip(vsix, zip => AddEntry(zip, "payload.exe", Bytes("MZ added after signing.")));

        // The extension exe has no content type, so OPC does not see the entry. The check does.
        Assert.DoesNotContain("/payload.exe", PartNames(vsix));
        Assert.Equal(VerifyResult.Success, VerifySignature(vsix));

        Assert.Equal(DoesNotCover("/payload.exe"), Check(vsix));
    }

    [Fact]
    public void APartThatWasLeftOutOfTheSignatureIsRefused()
    {
        var vsix = NewVsix();
        CreateUnsigned(vsix, package => AddPart(package, "/unsigned.txt", "text/plain", Bytes("The signer did not sign this part.")));
        Sign(vsix, TestCertificates.Installer, parts: package => ContentParts(package).Where(u => u.OriginalString != "/unsigned.txt").ToList());

        Assert.Equal(VerifyResult.Success, VerifySignature(vsix));
        Assert.Equal(DoesNotCover("/unsigned.txt"), Check(vsix));
    }

    [Fact]
    public void ASignatureThatSignsOnlySomePartsLeavesTheOthersUncovered()
    {
        var vsix = NewVsix();
        CreateUnsigned(vsix);
        Sign(vsix, TestCertificates.Installer, parts: package => new[] { PartUri(Manifest) });

        // Five parts are uncovered. The reason names the first three, in ordinal order, and counts the rest.
        Assert.Equal(
            DoesNotCover("/Dir/My%20File.TXT, /LICENSE.txt, /PlanViewer.Ssms.dll and 2 more"),
            Check(vsix));
    }

    [Fact]
    public void AnEntryThatIsNamedLikeAPartOfTheSignatureButIsNotThePartOfTheSignatureIsRefused()
    {
        var vsix = NewVsix();
        CreateSigned(vsix, TestCertificates.Installer);
        EditZip(vsix, zip => AddEntry(zip, "package/services/digital-signature/xml-signature/other.psdsxs", Bytes("<Signature />")));

        Assert.Equal(DoesNotCover("/package/services/digital-signature/xml-signature/other.psdsxs"), Check(vsix));
    }

    [Theory]
    [InlineData("Dir/")]
    [InlineData("../evil.bin")]
    [InlineData("Dir\\evil.bin")]
    public void AnEntryWithADirectoryNameOrAPathThatLeavesTheFolderIsRefused(string entryName)
    {
        var vsix = NewVsix();
        CreateSigned(vsix, TestCertificates.Installer);
        EditZip(vsix, zip => AddEntry(zip, entryName, Bytes("")));

        Assert.Equal(DoesNotCover("/" + entryName), Check(vsix));
    }

    [Fact]
    public void AControlCharacterInAnEntryNameIsShownAsAQuestionMark()
    {
        var vsix = NewVsix();
        CreateSigned(vsix, TestCertificates.Installer);

        // U+009B is the 8-bit form of the escape sequence introducer. ZipArchive and OPC accept it in a name.
        EditZip(vsix, zip => AddEntry(zip, "evil\u009b[31m.bin", Bytes("x")));

        // The reason is printed to the console, so it must not carry the control character itself.
        Assert.Equal(DoesNotCover("/evil?[31m.bin"), Check(vsix));
    }

    [Fact]
    public void AnEscapeCharacterInAnEntryNameMakesTheFileUnreadableAndIsRefused()
    {
        var vsix = NewVsix();
        CreateSigned(vsix, TestCertificates.Installer);
        EditZip(vsix, zip => AddEntry(zip, "evil_[31m.bin", Bytes("x")));

        // ZipArchive will not write an escape character in a name, so the test puts it in the bytes of the
        // file. A file that someone builds by hand can hold one, and the ZIP reader of .NET Framework refuses it.
        PatchEntryName(vsix, "evil_[31m.bin", "evil\u001b[31m.bin");

        Assert.StartsWith(FailedToRead, Check(vsix));
    }

    [Fact]
    public void ALongEntryNameIsCutShortInTheReason()
    {
        var vsix = NewVsix();
        CreateSigned(vsix, TestCertificates.Installer);
        var name = new string('a', 100) + ".bin";
        EditZip(vsix, zip => AddEntry(zip, name, Bytes("x")));

        Assert.Equal(DoesNotCover("/" + new string('a', 80) + "..."), Check(vsix));
    }
}
