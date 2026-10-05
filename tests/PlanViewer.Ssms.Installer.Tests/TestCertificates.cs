using System;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace PlanViewer.Ssms.Installer.Tests;

/// <summary>
/// Self-signed certificates that the tests make for themselves. Each one lives in memory only: no
/// certificate or key is committed, written to a file or added to a Windows certificate store.
/// </summary>
internal static class TestCertificates
{
    // CertificateRequest.CreateSelfSigned makes a certificate with an ephemeral CNG key. The
    // signing API of System.IO.Packaging signs with it as it is, on .NET Framework 4.8 as well as
    // on the CI runner, so there is no PFX export and import round trip and no key on disk.
    public static X509Certificate2 Create(string subject)
    {
        using (var rsa = RSA.Create(2048))
        {
            var request = new CertificateRequest("CN=" + subject, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
            var now = DateTimeOffset.UtcNow;
            return request.CreateSelfSigned(now.AddDays(-1), now.AddYears(1));
        }
    }

    /// <summary>The certificate that the tests treat as the certificate of the signed installer.</summary>
    public static X509Certificate2 Installer { get; } = Create("Test installer");

    /// <summary>A second certificate, for a VSIX that another signer signed.</summary>
    public static X509Certificate2 Other { get; } = Create("Other signer");

    /// <summary>
    /// The certificate as the installer holds it. X509Certificate.CreateFromSignedFile returns the
    /// public certificate as a plain X509Certificate, without a private key, and so does this.
    /// </summary>
    public static X509Certificate AsInstallerCertificate(X509Certificate2 certificate)
    {
        return new X509Certificate(certificate.Export(X509ContentType.Cert));
    }
}
