using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.IO.Packaging;
using System.Linq;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace PlanViewer.Ssms.Installer.Tests;

/// <summary>
/// Builds small VSIX packages at test time and edits them the way a signer, or someone who tampers
/// with a file, could. It builds the package with System.IO.Packaging and signs it with the signing
/// API of that library, PackageDigitalSignatureManager. It edits the ZIP entries directly where a
/// test needs something that OPC would not write. Nothing here is a checked-in binary.
/// </summary>
internal static class VsixFixture
{
    public const string ContentTypesEntry = "[Content_Types].xml";

    public const string SignatureRelationshipPrefix = "http://schemas.openxmlformats.org/package/2006/relationships/digital-signature/";
    public const string OriginRelationship = SignatureRelationshipPrefix + "origin";
    public const string CertificateRelationship = SignatureRelationshipPrefix + "certificate";
    public const string OriginContentType = "application/vnd.openxmlformats-package.digital-signature-origin";
    public const string CertificateContentType = "application/vnd.openxmlformats-package.digital-signature-certificate";

    /// <summary>A relationship type of the tests. It has no meaning to OPC.</summary>
    public const string TestRelationship = "http://example.com/relationships/related";

    /// <summary>The folder that System.IO.Packaging puts the parts of a signature in.</summary>
    public const string SignatureFolder = "/package/services/digital-signature/";

    /// <summary>The origin part that System.IO.Packaging creates, when a test does not make its own.</summary>
    public const string OriginPart = SignatureFolder + "origin.psdsor";

    // The parts of the extension that the tests build. The mixed case and the escape in the last
    // name are deliberate: entry names are compared exactly, as they are written.
    public const string Manifest = "/extension.vsixmanifest";
    public const string Assembly = "/PlanViewer.Ssms.dll";
    public const string License = "/LICENSE.txt";
    public const string MixedCase = "/Dir/My%20File.TXT";

    static readonly (string Name, string ContentType, string Text)[] DefaultParts =
    {
        (Manifest, "text/xml", "<PackageManifest Version=\"2.0.0\" />"),
        ("/catalog.json", "application/json", "{ \"manifestVersion\": \"1.1\" }"),
        (Assembly, "application/octet-stream", "MZ this stands in for the assembly"),
        ("/PlanViewer.Ssms.pkgdef", "text/plain", "[$RootKey$\\Packages]"),
        (License, "text/plain", "The license text."),
        (MixedCase, "text/plain", "A part with mixed case and an escape in its name."),
    };

    /// <summary>The names of the parts that <see cref="CreateUnsigned"/> makes.</summary>
    public static List<string> DefaultPartNames => DefaultParts.Select(p => p.Name).ToList();

    public static byte[] Bytes(string text) => Encoding.UTF8.GetBytes(text);

    public static Uri PartUri(string name) => new Uri(name, UriKind.Relative);

    /// <summary>Makes an unsigned package that holds the parts of a small extension. Nothing is signed.</summary>
    public static void CreateUnsigned(string path, Action<Package>? addMore = null)
    {
        using (var package = Package.Open(path, FileMode.Create, FileAccess.ReadWrite))
        {
            foreach (var part in DefaultParts)
                AddPart(package, part.Name, part.ContentType, Bytes(part.Text));
            addMore?.Invoke(package);
        }
    }

    public static PackagePart AddPart(Package package, string name, string contentType, byte[] bytes)
    {
        var part = package.CreatePart(PartUri(name), contentType);
        using (var stream = part.GetStream(FileMode.Create, FileAccess.Write))
            stream.Write(bytes, 0, bytes.Length);
        return part;
    }

    /// <summary>The parts that a signer signs when it signs the content: no relationship parts and no signature parts.</summary>
    public static List<Uri> ContentParts(Package package)
    {
        return package.GetParts()
            .Select(p => p.Uri)
            .Where(u => !PackUriHelper.IsRelationshipPartUri(u))
            .Where(u => !u.OriginalString.StartsWith(SignatureFolder, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    /// <summary>
    /// Signs the package in place with the signing API of System.IO.Packaging. Without a list of parts it
    /// signs every content part. It signs the relationship parts and single relationships only when the
    /// test passes them.
    /// </summary>
    public static void Sign(
        string path,
        X509Certificate2 certificate,
        CertificateEmbeddingOption embedding = CertificateEmbeddingOption.InSignaturePart,
        Func<Package, IEnumerable<Uri>>? parts = null,
        IEnumerable<PackageRelationshipSelector>? selectors = null)
    {
        using (var package = Package.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var manager = new PackageDigitalSignatureManager(package) { CertificateOption = embedding };
            var signedParts = (parts ?? ContentParts)(package).ToList();
            if (selectors == null)
                manager.Sign(signedParts, certificate);
            else
                manager.Sign(signedParts, certificate, selectors.ToList());
        }
    }

    /// <summary>Signs every content part and, on top of those, the relationship part of each source that the test names.</summary>
    public static Func<Package, IEnumerable<Uri>> ContentAndRelationshipParts(params string[] sources)
    {
        return package => ContentParts(package)
            .Concat(sources.Select(s => PackUriHelper.GetRelationshipPartUri(PartUri(s))))
            .ToList();
    }

    /// <summary>Builds a package with the default parts and signs all of them with the certificate.</summary>
    public static void CreateSigned(string path, X509Certificate2 certificate, CertificateEmbeddingOption embedding = CertificateEmbeddingOption.InSignaturePart)
    {
        CreateUnsigned(path);
        Sign(path, certificate, embedding);
    }

    /// <summary>Opens a package that may be signed, for changes through System.IO.Packaging.</summary>
    public static void EditPackage(string path, Action<Package> edit)
    {
        using (var package = Package.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            edit(package);
    }

    /// <summary>The signature part of the first signature of the package.</summary>
    public static PackagePart SignaturePart(Package package) => new PackageDigitalSignatureManager(package).Signatures[0].SignaturePart;

    /// <summary>Adds a relationship to a part that exists, or to the package when the source is "/".</summary>
    public static void AddRelationship(string path, string source, string id, string type, string target, TargetMode mode = TargetMode.Internal)
    {
        EditPackage(path, package =>
        {
            var targetUri = new Uri(target, mode == TargetMode.Internal ? UriKind.Relative : UriKind.Absolute);
            if (source == "/")
                package.CreateRelationship(targetUri, mode, type, id);
            else
                package.GetPart(PartUri(source)).CreateRelationship(targetUri, mode, type, id);
        });
    }

    /// <summary>
    /// Opens the ZIP file for changes to its entries. Close the package before this: the file has to
    /// be free, and the package writes its parts when it closes.
    /// </summary>
    public static void EditZip(string path, Action<ZipArchive> edit)
    {
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Update))
            edit(zip);
    }

    public static List<string> EntryNames(string path)
    {
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Read))
            return zip.Entries.Select(e => e.FullName).ToList();
    }

    /// <summary>The one ZIP entry that has this extension. It fails when there is none or more than one.</summary>
    public static string EntryWithExtension(string path, string extension)
    {
        return EntryNames(path).Single(n => n.EndsWith(extension, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The same, inside a ZIP file that a test has open for changes.</summary>
    public static string EntryWithExtension(ZipArchive zip, string extension)
    {
        return zip.Entries.Select(e => e.FullName).Single(n => n.EndsWith(extension, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The names of the parts that OPC lists in the package. An entry without a content type is not among them.</summary>
    public static List<string> PartNames(string path)
    {
        using (var package = Package.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            return package.GetParts().Select(p => p.Uri.OriginalString).ToList();
    }

    /// <summary>The ZIP entries of the parts that the signatures of the file sign.</summary>
    public static List<string> SignedEntryNames(string path)
    {
        using (var package = Package.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            return new PackageDigitalSignatureManager(package).Signatures
                .SelectMany(s => s.SignedParts)
                .Select(EntryName)
                .ToList();
        }
    }

    public static byte[] ReadEntry(ZipArchive zip, string name)
    {
        using (var stream = zip.GetEntry(name)!.Open())
        using (var copy = new MemoryStream())
        {
            stream.CopyTo(copy);
            return copy.ToArray();
        }
    }

    /// <summary>Adds an entry. It does not check for an entry with the same name, so a test can add a second one.</summary>
    public static void AddEntry(ZipArchive zip, string name, byte[] bytes)
    {
        var entry = zip.CreateEntry(name);
        if (bytes.Length == 0)
            return;
        using (var stream = entry.Open())
            stream.Write(bytes, 0, bytes.Length);
    }

    /// <summary>Replaces the bytes of an entry by deleting it and adding it again under the same name.</summary>
    public static void ReplaceEntry(ZipArchive zip, string name, byte[] bytes)
    {
        zip.GetEntry(name)!.Delete();
        AddEntry(zip, name, bytes);
    }

    public static void RenameEntry(ZipArchive zip, string name, string newName)
    {
        var bytes = ReadEntry(zip, name);
        zip.GetEntry(name)!.Delete();
        AddEntry(zip, newName, bytes);
    }

    /// <summary>
    /// Changes an entry name in the bytes of the ZIP file, in the local header and in the central directory,
    /// without any check. ZipArchive refuses to write some names, such as a name with a control character,
    /// and a file that someone builds by hand can still hold one. Both names must have the same length.
    /// </summary>
    public static void PatchEntryName(string path, string name, string patchedName)
    {
        var oldBytes = Encoding.UTF8.GetBytes(name);
        var newBytes = Encoding.UTF8.GetBytes(patchedName);
        if (oldBytes.Length != newBytes.Length)
            throw new ArgumentException("The patched name must have the same length in bytes.");

        var file = File.ReadAllBytes(path);
        var found = 0;
        for (var i = 0; i <= file.Length - oldBytes.Length; i++)
        {
            var match = true;
            for (var j = 0; j < oldBytes.Length && match; j++)
                match = file[i + j] == oldBytes[j];
            if (!match)
                continue;
            Array.Copy(newBytes, 0, file, i, newBytes.Length);
            found++;
            i += oldBytes.Length - 1;
        }

        if (found != 2)
            throw new InvalidOperationException($"Expected the name {name} twice in the ZIP file (local header and central directory), found {found}.");
        File.WriteAllBytes(path, file);
    }

    /// <summary>The name of the ZIP entry that holds the part: its name without the leading slash.</summary>
    public static string EntryName(Uri partUri) => EntryName(partUri.OriginalString);

    public static string EntryName(string partName) => partName.Substring(1);

    /// <summary>Changes the first character of the signature value in the signature part, so that the signature no longer matches.</summary>
    public static void FlipSignatureValue(ZipArchive zip)
    {
        var name = EntryWithExtension(zip, ".psdsxs");
        var text = Encoding.UTF8.GetString(ReadEntry(zip, name));
        var match = System.Text.RegularExpressions.Regex.Match(text, "<SignatureValue[^>]*>([A-Za-z0-9+/])");
        if (!match.Success)
            throw new InvalidOperationException("The signature part has no signature value.");
        var first = match.Groups[1];
        var changed = first.Value == "A" ? "B" : "A";
        ReplaceEntry(zip, name, Bytes(text.Substring(0, first.Index) + changed + text.Substring(first.Index + 1)));
    }

    /// <summary>The text of an entry, changed by a text replacement, written back under the same name.</summary>
    public static void ReplaceInEntry(ZipArchive zip, string name, string oldText, string newText)
    {
        var text = Encoding.UTF8.GetString(ReadEntry(zip, name));
        if (!text.Contains(oldText))
            throw new InvalidOperationException($"The entry {name} has no text {oldText}.");
        ReplaceEntry(zip, name, Bytes(text.Replace(oldText, newText)));
    }
}
