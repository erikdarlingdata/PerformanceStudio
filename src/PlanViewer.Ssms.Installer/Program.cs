using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
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

        // Relationship types of the OPC package signature: origin, signature and certificate.
        const string SignatureRelationshipPrefix = "http://schemas.openxmlformats.org/package/2006/relationships/digital-signature/";
        const string CertificateRelationship = SignatureRelationshipPrefix + "certificate";

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
        // exactly one valid signature, made with installerCert, that covers everything in the package.
        internal static string CheckVsix(string vsixPath, X509Certificate installerCert)
        {
            try
            {
                using (var package = Package.Open(vsixPath, FileMode.Open, FileAccess.Read))
                {
                    var manager = new PackageDigitalSignatureManager(package);
                    if (manager.Signatures.Count == 0)
                        return "the file is not signed";
                    if (manager.Signatures.Count > 1)
                        return "the file has more than one signature";

                    var result = manager.VerifySignatures(false);
                    if (result != VerifyResult.Success)
                        return $"the signature is not valid ({result})";

                    var signature = manager.Signatures[0];
                    if (signature.Signer == null || signature.Signer.GetCertHashString() != installerCert.GetCertHashString())
                        return "the file is signed with a different certificate than this installer";

                    var uncovered = FindUncovered(package, manager, signature);
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

        // Lists the parts and relationships that the signature does not cover. Its own parts (origin,
        // signature, certificates) and its own relationships are left out. Signers differ in how they
        // sign relationship parts: some sign the whole part, some select single relationships.
        static List<string> FindUncovered(Package package, PackageDigitalSignatureManager manager, PackageDigitalSignature signature)
        {
            var signed = new HashSet<string>(signature.SignedParts.Select(PartKey));
            var own = new HashSet<string> { PartKey(manager.SignatureOrigin), PartKey(signature.SignaturePart.Uri) };
            foreach (var rel in signature.SignaturePart.GetRelationshipsByType(CertificateRelationship))
            {
                var certUri = PackUriHelper.ResolvePartUri(signature.SignaturePart.Uri, rel.TargetUri);
                if (IsCertificate(package.GetPart(certUri)))
                    own.Add(PartKey(certUri));
            }

            var uncovered = new List<string>();
            var parts = package.GetParts().Where(p => !PackUriHelper.IsRelationshipPartUri(p.Uri)).ToList();
            foreach (var part in parts)
            {
                if (!own.Contains(PartKey(part.Uri)) && !signed.Contains(PartKey(part.Uri)))
                    uncovered.Add(part.Uri.ToString());
            }

            var selected = new HashSet<string>(signature.SignedRelationshipSelectors.SelectMany(s => s.Select(package)).Select(RelationshipKey));
            var signedWhole = new HashSet<string>(signature.SignedParts
                .Where(PackUriHelper.IsRelationshipPartUri)
                .Select(u => PartKey(PackUriHelper.GetSourcePartUriFromRelationshipPartUri(u))));
            foreach (var rel in package.GetRelationships().Concat(parts.SelectMany(p => p.GetRelationships())))
            {
                if (!rel.RelationshipType.StartsWith(SignatureRelationshipPrefix, StringComparison.Ordinal)
                    && !selected.Contains(RelationshipKey(rel))
                    && !signedWhole.Contains(PartKey(rel.SourceUri)))
                    uncovered.Add($"relationship {rel.Id} of {rel.SourceUri}");
            }

            return uncovered;
        }

        // Part names are not case-sensitive and may be escaped. "/" is the package itself, the source of package relationships.
        static string PartKey(Uri partUri) => Uri.UnescapeDataString(partUri.ToString()).ToUpperInvariant();

        static string RelationshipKey(PackageRelationship rel) => rel.SourceUri + " " + rel.Id;

        static bool IsCertificate(PackagePart part)
        {
            try
            {
                using (var stream = part.GetStream())
                using (var bytes = new MemoryStream())
                {
                    stream.CopyTo(bytes);
                    new X509Certificate(bytes.ToArray());
                    return true;
                }
            }
            catch (CryptographicException)
            {
                return false;
            }
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
