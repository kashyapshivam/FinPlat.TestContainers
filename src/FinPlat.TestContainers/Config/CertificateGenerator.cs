using System;
using System.IO;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace FinPlat.TestContainers.Config;

/// <summary>
/// Generates self-signed TLS certificates for the SLT test environment.
/// Certificates include SANs for all storage endpoints and authority hosts.
/// </summary>
public static class CertificateGenerator
{
    /// <summary>
    /// Generates a self-signed certificate with SANs covering storage services and auth hosts.
    /// </summary>
    /// <param name="options">Azurite options defining account name, suffix, and authority hosts.</param>
    /// <returns>A <see cref="CertificateMaterial"/> containing PEM-encoded cert and key.</returns>
    public static CertificateMaterial Generate(AzuriteOptions options)
    {
        using var rsa = RSA.Create(2048);
        var subject = new X500DistinguishedName($"CN={options.EndpointSuffix}");

        var request = new CertificateRequest(subject, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        // Basic constraints: CA=true (so it can be trusted as a root)
        request.CertificateExtensions.Add(
            new X509BasicConstraintsExtension(true, false, 0, true));

        // Subject Alternative Names
        var sanBuilder = new SubjectAlternativeNameBuilder();
        sanBuilder.AddDnsName(options.EndpointSuffix);
        sanBuilder.AddDnsName($"*.blob.{options.EndpointSuffix}");
        sanBuilder.AddDnsName($"*.queue.{options.EndpointSuffix}");
        sanBuilder.AddDnsName($"*.table.{options.EndpointSuffix}");

        foreach (var host in options.AuthorityHosts)
        {
            sanBuilder.AddDnsName(host);
        }

        request.CertificateExtensions.Add(sanBuilder.Build());

        // Self-sign with 10 year validity
        var cert = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1),
            DateTimeOffset.UtcNow.AddYears(10));

        // Export as PEM
        var certPem = cert.ExportCertificatePem();
        var keyPem = rsa.ExportRSAPrivateKeyPem();

        return new CertificateMaterial(certPem, keyPem);
    }
}

/// <summary>
/// Holds PEM-encoded certificate and private key material.
/// </summary>
public class CertificateMaterial : IDisposable
{
    private string? _certTempPath;
    private string? _keyTempPath;

    /// <summary>PEM-encoded certificate.</summary>
    public string CertPem { get; }

    /// <summary>PEM-encoded RSA private key.</summary>
    public string KeyPem { get; }

    internal CertificateMaterial(string certPem, string keyPem)
    {
        CertPem = certPem;
        KeyPem = keyPem;
    }

    /// <summary>
    /// Writes cert and key to temporary files and returns their paths.
    /// Files are cleaned up on <see cref="Dispose"/>.
    /// </summary>
    public (string CertPath, string KeyPath) WriteTempFiles()
    {
        if (_certTempPath is null)
        {
            var dir = Path.Combine(Path.GetTempPath(), $"finplat-test-certs-{Guid.NewGuid():N}");
            Directory.CreateDirectory(dir);

            _certTempPath = Path.Combine(dir, "cert.pem");
            _keyTempPath = Path.Combine(dir, "key.pem");

            File.WriteAllText(_certTempPath, CertPem);
            File.WriteAllText(_keyTempPath, KeyPem);
        }

        return (_certTempPath, _keyTempPath!);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_certTempPath is not null)
        {
            var dir = Path.GetDirectoryName(_certTempPath)!;
            try { Directory.Delete(dir, true); } catch { /* best-effort cleanup */ }
            _certTempPath = null;
            _keyTempPath = null;
        }
    }
}
