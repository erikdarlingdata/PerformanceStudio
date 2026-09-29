using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.IO.Packaging;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace PlanViewer.Ssms.Installer
{
    class Program
    {
        static readonly (string Label, string VsixInstallerPath)[] SsmsVersions =
        {
            ("SSMS 22", @"C:\Program Files\Microsoft SQL Server Management Studio 22\Release\Common7\IDE\VSIXInstaller.exe"),
            ("SSMS 21", @"C:\Program Files\Microsoft SQL Server Management Studio 21\Common7\IDE\VSIXInstaller.exe"),
        };

        // Relationship types and content type of the OPC package signature.
        const string SignatureRelationshipPrefix = "http://schemas.openxmlformats.org/package/2006/relationships/digital-signature/";
        const string OriginRelationship = SignatureRelationshipPrefix + "origin";
        const string CertificateRelationship = SignatureRelationshipPrefix + "certificate";
        const string CertificateContentType = "application/vnd.openxmlformats-package.digital-signature-certificate";

        // The one ZIP entry that OPC does not treat as a part.
        const string ContentTypesEntry = "[Content_Types].xml";

        // The source of the relationships of the package itself.
        static readonly Uri PackageRoot = new Uri("/", UriKind.Relative);

        static int Main(string[] args)
        {
            if (args.Length > 0 && string.Equals(args[0], "--verify-only", StringComparison.OrdinalIgnoreCase))
                return VerifyOnly(args.Length > 1 ? args[1] : null);

            Console.WriteLine("===========================================");
            Console.WriteLine(" Performance Studio — SSMS Extension");
            Console.WriteLine("===========================================");
            Console.WriteLine();

            var vsixPath = FindVsix(args);
            if (vsixPath == null)
            {
                Console.WriteLine("ERROR: Could not find PlanViewer.Ssms.vsix.");
                Console.WriteLine("Place it in the same folder as this installer, or pass the path as an argument.");
                WaitForKey();
                return 1;
            }

            Console.WriteLine($"VSIX: {vsixPath}");
            Console.WriteLine();

            var installed = SsmsVersions.Where(v => File.Exists(v.VsixInstallerPath)).ToArray();
            if (installed.Length == 0)
            {
                Console.WriteLine("ERROR: No supported SSMS installation found.");
                Console.WriteLine("Supported: SSMS 21, SSMS 22");
                WaitForKey();
                return 1;
            }

            string tempDir = null;
            bool anyFailed = false;
            try
            {
                var installerCert = GetSigner(Assembly.GetExecutingAssembly().Location);
                if (installerCert == null)
                {
                    Console.WriteLine("This installer is not signed, so the VSIX signature is not checked.");
                    Console.WriteLine();
                }
                else
                {
                    // A signed installer installs only a VSIX signed with the same certificate.
                    // The check runs on a private copy, and that copy is the file that gets installed.
                    string error;
                    try
                    {
                        tempDir = Path.Combine(Path.GetTempPath(), "PlanViewerSsms-" + Guid.NewGuid().ToString("N"));
                        Directory.CreateDirectory(tempDir);
                        var copy = Path.Combine(tempDir, Path.GetFileName(vsixPath));
                        File.Copy(vsixPath, copy);
                        vsixPath = copy;
                        error = CheckVsix(copy, installerCert);
                    }
                    catch (Exception ex)
                    {
                        error = $"the installer failed to copy the file for the check ({ex.Message})";
                    }

                    if (error != null)
                    {
                        DeleteFolder(tempDir);
                        Console.WriteLine($"ERROR: {Path.GetFileName(vsixPath)} failed the signature check: {error}.");
                        Console.WriteLine("Nothing was installed.");
                        Console.WriteLine("Download InstallSsmsExtension.exe and PlanViewer.Ssms.vsix again from the same release, and keep them in one folder.");
                        Console.WriteLine("You can also double-click PlanViewer.Ssms.vsix to install it.");
                        WaitForKey();
                        return 1;
                    }

                    Console.WriteLine("The VSIX is signed with the same certificate as this installer.");
                    Console.WriteLine();
                }

                foreach (var (label, installerPath) in installed)
                {
                    Console.WriteLine($"Found {label} — installing...");

                    var psi = new ProcessStartInfo
                    {
                        FileName = installerPath,
                        Arguments = $"/admin \"{vsixPath}\"",
                        UseShellExecute = false,
                    };

                    try
                    {
                        var proc = Process.Start(psi);
                        proc.WaitForExit();

                        if (proc.ExitCode == 0)
                        {
                            Console.WriteLine($"  OK — installed into {label}. Restart SSMS to activate.");
                        }
                        else
                        {
                            Console.WriteLine($"  FAILED (exit code {proc.ExitCode}).");
                            anyFailed = true;
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"  FAILED: {ex.Message}");
                        anyFailed = true;
                    }
                    Console.WriteLine();
                }
            }
            finally
            {
                DeleteFolder(tempDir);
            }

            if (anyFailed)
            {
                Console.WriteLine("One or more installations failed.");
                WaitForKey();
                return 1;
            }

            Console.WriteLine("Done. Restart SSMS to activate the extension.");
            WaitForKey();
            return 0;
        }

        // Checks the VSIX against this installer's certificate without installing anything.
        static int VerifyOnly(string vsixPath)
        {
            if (vsixPath == null || !File.Exists(vsixPath))
            {
                Console.WriteLine("ERROR: --verify-only needs the path of a .vsix file.");
                return 1;
            }

            var installerCert = GetSigner(Assembly.GetExecutingAssembly().Location);
            if (installerCert == null)
            {
                Console.WriteLine("This installer is not signed, so the VSIX signature is not checked.");
                return 0;
            }

            var error = CheckVsix(vsixPath, installerCert);
            if (error != null)
            {
                Console.WriteLine($"ERROR: {Path.GetFileName(vsixPath)} failed the signature check: {error}.");
                return 1;
            }

            Console.WriteLine("The VSIX is signed with the same certificate as this installer.");
            return 0;
        }

        // The Authenticode certificate of the installer, or null when the installer is not signed.
        // This reads the certificate only. It does not validate the signature.
        internal static X509Certificate GetSigner(string exePath)
        {
            try { return X509Certificate.CreateFromSignedFile(exePath); }
            catch (CryptographicException) { return null; }
        }

        // Returns null when the VSIX passes, or a short reason when it does not. The VSIX passes when it has
        // exactly one valid signature, made with installerCert, and the signature covers every entry in the ZIP file.
        internal static string CheckVsix(string vsixPath, X509Certificate installerCert)
        {
            try
            {
                // One read of the file feeds both views of it: the OPC package and the raw ZIP entries.
                var file = File.ReadAllBytes(vsixPath);
                using (var package = Package.Open(new MemoryStream(file), FileMode.Open, FileAccess.Read))
                {
                    var manager = new PackageDigitalSignatureManager(package);
                    if (manager.Signatures.Count == 0)
                        return "the file is not signed";
                    if (manager.Signatures.Count > 1)
                        return "the file has more than one signature";

                    var result = manager.VerifySignatures(false);
                    if (result != VerifyResult.Success)
                        return $"the signature is not valid ({result})";

                    // The whole certificate must match, not only its thumbprint.
                    var signature = manager.Signatures[0];
                    var certificate = installerCert.GetRawCertData();
                    if (signature.Signer == null || !signature.Signer.GetRawCertData().SequenceEqual(certificate))
                        return "the file is signed with a different certificate than this installer";

                    var uncovered = FindUncovered(file, package, manager, signature, certificate);
                    if (uncovered.Count > 0)
                        return "the signature does not cover " + string.Join(", ", uncovered.Take(3)) + (uncovered.Count > 3 ? $" and {uncovered.Count - 3} more" : "");

                    return null;
                }
            }
            catch (Exception ex)
            {
                return $"the installer failed to read the file ({ex.Message})";
            }
        }

        // Lists what the signature does not cover. The list starts from the raw ZIP entries, not from the parts that
        // OPC finds. Names are compared exactly, so names that differ only in case are different names. Every entry
        // must be one of these:
        //   - a part that the signature signs;
        //   - [Content_Types].xml;
        //   - a relationship part that the signature covers, or that belongs to the origin part or the signature part;
        //   - the origin part, when it is signed or empty;
        //   - the signature part;
        //   - a certificate part that holds exactly the certificate of the installer.
        // A relationship part is covered when it is signed whole, or when the signature selects every relationship
        // in it. The origin relationship of the package is the one exception: some signers add it after signing, so
        // it needs no cover. The relationships of the origin part and the signature part may point only to entries
        // in this list. An entry is never classified by parsing what is in it. The only reads are the checks that the
        // origin part is empty and that a certificate part is the certificate of the installer.
        static List<string> FindUncovered(byte[] file, Package package, PackageDigitalSignatureManager manager, PackageDigitalSignature signature, byte[] certificate)
        {
            var signedNames = new HashSet<string>(signature.SignedParts.Select(EntryName), StringComparer.Ordinal);
            var originName = EntryName(manager.SignatureOrigin);
            var signatureName = EntryName(signature.SignaturePart.Uri);

            // A certificate part is the target of a certificate relationship from the signature part and has the certificate content type.
            var certificateNames = new HashSet<string>(StringComparer.Ordinal);
            foreach (var rel in signature.SignaturePart.GetRelationshipsByType(CertificateRelationship))
            {
                if (rel.TargetMode != TargetMode.Internal)
                    continue;
                var target = PackUriHelper.ResolvePartUri(signature.SignaturePart.Uri, rel.TargetUri);
                if (package.PartExists(target) && string.Equals(package.GetPart(target).ContentType, CertificateContentType, StringComparison.OrdinalIgnoreCase))
                    certificateNames.Add(EntryName(target));
            }

            // Relationship parts. Signers differ in how they cover them: some sign the whole part, some select
            // single relationships.
            var allowed = new HashSet<string>(signedNames, StringComparer.Ordinal) { ContentTypesEntry, signatureName };
            var selected = new HashSet<string>(signature.SignedRelationshipSelectors.SelectMany(s => s.Select(package)).Select(RelationshipKey));
            var sources = new List<(Uri Uri, IEnumerable<PackageRelationship> Relationships)> { (PackageRoot, package.GetRelationships()) };
            foreach (var part in package.GetParts().Where(p => !PackUriHelper.IsRelationshipPartUri(p.Uri)))
                sources.Add((part.Uri, part.GetRelationships()));

            var relationshipFindings = new List<string>();
            var explained = new HashSet<string>(StringComparer.Ordinal);
            foreach (var source in sources)
            {
                var sourceName = EntryName(source.Uri);
                var relationshipsName = EntryName(PackUriHelper.GetRelationshipPartUri(source.Uri));
                if (sourceName == originName || sourceName == signatureName)
                {
                    // These two relationship parts are unsigned. Where their relationships point is checked below.
                    allowed.Add(relationshipsName);
                    continue;
                }

                if (signedNames.Contains(relationshipsName))
                    continue;

                var covered = true;
                foreach (var rel in source.Relationships)
                {
                    var isOrigin = sourceName.Length == 0 && rel.RelationshipType == OriginRelationship && rel.TargetMode == TargetMode.Internal
                        && EntryName(PackUriHelper.ResolvePartUri(source.Uri, rel.TargetUri)) == originName;
                    if (isOrigin || selected.Contains(RelationshipKey(rel)))
                        continue;
                    relationshipFindings.Add($"relationship {rel.Id} of {rel.SourceUri}");
                    covered = false;
                }

                if (covered)
                    allowed.Add(relationshipsName);
                else
                    explained.Add(relationshipsName);
            }

            var uncovered = new List<string>();
            var accepted = new HashSet<string>(StringComparer.Ordinal);
            using (var zip = new ZipArchive(new MemoryStream(file), ZipArchiveMode.Read))
            {
                // Names that differ only in case are one file when the VSIX is unpacked on Windows.
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var entry in zip.Entries)
                {
                    var name = entry.FullName;
                    if (!seen.Add(name))
                        uncovered.Add(Display(name) + " (a second entry with the same name)");
                    else if (allowed.Contains(name)
                        || (name == originName && HasContent(entry, new byte[0]))
                        || (certificateNames.Contains(name) && HasContent(entry, certificate)))
                        accepted.Add(name);
                    else if (!explained.Contains(name))
                        uncovered.Add(Display(name));
                }
            }

            uncovered.Sort(StringComparer.Ordinal);
            uncovered.AddRange(relationshipFindings);

            foreach (var partUri in new[] { manager.SignatureOrigin, signature.SignaturePart.Uri })
            {
                if (!package.PartExists(partUri))
                    continue;
                foreach (var rel in package.GetPart(partUri).GetRelationships())
                {
                    if (rel.TargetMode != TargetMode.Internal || !accepted.Contains(EntryName(PackUriHelper.ResolvePartUri(rel.SourceUri, rel.TargetUri))))
                        uncovered.Add($"relationship {rel.Id} of {rel.SourceUri}");
                }
            }

            return uncovered;
        }

        // The name of the ZIP entry that holds a part: the part name without its leading slash.
        // The package itself has the name "".
        static string EntryName(Uri partUri)
        {
            var name = partUri.OriginalString;
            if (!name.StartsWith("/", StringComparison.Ordinal))
                throw new InvalidDataException($"the part name {name} is not valid");
            return name.Substring(1);
        }

        static string RelationshipKey(PackageRelationship rel) => rel.SourceUri + " " + rel.Id;

        // True when the entry holds exactly these bytes. It reads no more than one byte past the expected length.
        static bool HasContent(ZipArchiveEntry entry, byte[] expected)
        {
            using (var stream = entry.Open())
            {
                var actual = new byte[expected.Length + 1];
                var length = 0;
                int read;
                while (length < actual.Length && (read = stream.Read(actual, length, actual.Length - length)) > 0)
                    length += read;
                return length == expected.Length && actual.Take(length).SequenceEqual(expected);
            }
        }

        // An entry name as it appears in a message, like a part name. Control characters and long names are cut down.
        static string Display(string entryName)
        {
            var shown = new string(entryName.Select(c => char.IsControl(c) ? '?' : c).ToArray());
            return "/" + (shown.Length > 80 ? shown.Substring(0, 80) + "..." : shown);
        }

        static void DeleteFolder(string path)
        {
            if (path == null)
                return;
            try { Directory.Delete(path, true); } catch { }
        }

        static string FindVsix(string[] args)
        {
            // 1. Explicit argument
            if (args.Length > 0 && File.Exists(args[0]))
                return Path.GetFullPath(args[0]);

            // 2. Same directory as this exe
            var exeDir = AppDomain.CurrentDomain.BaseDirectory;
            var candidate = Path.Combine(exeDir, "PlanViewer.Ssms.vsix");
            if (File.Exists(candidate))
                return candidate;

#if DEBUG
            // 3. Dev builds only: look in common build output locations relative
            //    to exe. A release build uses only the argument or the exe's folder.
            foreach (var sub in new[] { ".", @"..\bin\Release", @"..\bin\Debug" })
            {
                candidate = Path.GetFullPath(Path.Combine(exeDir, sub, "PlanViewer.Ssms.vsix"));
                if (File.Exists(candidate))
                    return candidate;
            }
#endif

            return null;
        }

        static void WaitForKey()
        {
            Console.WriteLine();
            Console.WriteLine("Press any key to exit...");
            try { Console.ReadKey(true); } catch { }
        }
    }
}
