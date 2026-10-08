using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace ApiaryCopilot.UnitTests.Api.SignIn;

// A self-signed certificate made in memory for one test, as the DataProtection__Certificate setting carries it: a
// PKCS#12 in base64. Synthetic and throwaway - never written to disk, never committed, never a real secret.
internal static class SyntheticCertificate
{
    public static string Pkcs12Base64(string? password = null, bool withPrivateKey = true, int keySize = 2048)
    {
        using var rsa = RSA.Create(keySize);
        var request = new CertificateRequest(
            "CN=apiary-keeper-unit-tests-data-protection", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(1));
        if (!withPrivateKey)
        {
            using var publicOnly = X509CertificateLoader.LoadCertificate(certificate.Export(X509ContentType.Cert));
            return Convert.ToBase64String(publicOnly.Export(X509ContentType.Pkcs12, password));
        }

        return Convert.ToBase64String(certificate.Export(X509ContentType.Pkcs12, password));
    }

    public static string EcdsaPkcs12Base64()
    {
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=apiary-keeper-unit-tests-ecdsa", ecdsa, HashAlgorithmName.SHA256);
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(1));
        return Convert.ToBase64String(certificate.Export(X509ContentType.Pkcs12));
    }
}
