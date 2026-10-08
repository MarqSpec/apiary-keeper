using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace ApiaryCopilot.Api.SignIn;

/// <summary>
/// The certificate that encrypts the Data Protection key ring at rest in PostgreSQL (D-19), set as
/// <c>DataProtection__Certificate</c> (a PKCS#12, base64) and, when it has one, <c>DataProtection__CertificatePassword</c>
/// (DEPLOYMENT.md §3). Required whenever sign-in is configured, since the default key ring is unencrypted. Both are
/// secrets: never logged, echoed or returned, and a failure names the setting, never its value.
/// </summary>
public sealed class KeyRingOptions
{
    /// <summary>The configuration section: <c>DataProtection</c>.</summary>
    public const string SectionName = "DataProtection";

    /// <summary>The shortest RSA key accepted.</summary>
    public const int MinRsaKeyBits = 2048;

    /// <summary>The certificate with its private key, as a base64 PKCS#12.</summary>
    public string? Certificate { get; set; }

    /// <summary>The PKCS#12's password, when it has one.</summary>
    public string? CertificatePassword { get; set; }

    /// <summary>The failures; empty when the certificate loads with an RSA private key of at least 2048 bits.</summary>
    public IEnumerable<string> Validate()
    {
        X509Certificate2 certificate;
        try
        {
            certificate = LoadCertificate();
        }
        catch (FormatException)
        {
            return ["DataProtection__Certificate must be set to the key ring certificate as a base64 PKCS#12."];
        }
        catch (CryptographicException) when (!string.IsNullOrEmpty(CertificatePassword))
        {
            return ["DataProtection__Certificate did not load: it is not a PKCS#12, or DataProtection__CertificatePassword is not its password."];
        }
        catch (CryptographicException)
        {
            return ["DataProtection__Certificate did not load as a PKCS#12; if it has a password, set DataProtection__CertificatePassword."];
        }

        using (certificate)
        {
            if (!certificate.HasPrivateKey)
            {
                return ["DataProtection__Certificate must include its private key, or the key ring could not be read back."];
            }

            using var rsa = certificate.GetRSAPrivateKey();
            if (rsa is null)
            {
                return ["DataProtection__Certificate must hold an RSA key: Data Protection encrypts its keys with RSA."];
            }

            return rsa.KeySize < MinRsaKeyBits
                ? [$"DataProtection__Certificate's RSA key must be at least {MinRsaKeyBits} bits."]
                : [];
        }
    }

    /// <summary>Loads the certificate, its private key held in memory only.</summary>
    /// <exception cref="FormatException">The setting is missing or not base64.</exception>
    /// <exception cref="CryptographicException">It is not a PKCS#12, or the password is wrong.</exception>
    public X509Certificate2 LoadCertificate()
    {
        if (string.IsNullOrWhiteSpace(Certificate))
        {
            throw new FormatException("DataProtection__Certificate is not set.");
        }

        var bytes = Convert.FromBase64String(Certificate.Trim());
        return X509CertificateLoader.LoadPkcs12(
            bytes, string.IsNullOrEmpty(CertificatePassword) ? null : CertificatePassword, X509KeyStorageFlags.EphemeralKeySet);
    }
}
