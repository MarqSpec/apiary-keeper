using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using ApiaryCopilot.Api.SignIn;

namespace ApiaryCopilot.UnitTests.Api.SignIn;

// DataProtection__Certificate (DEPLOYMENT.md §3): the X.509 certificate, from the environment, that encrypts the Data
// Protection key ring at rest in PostgreSQL (D-19). Checked at startup whenever sign-in is on; a failure names the
// setting and never its value.
public sealed class KeyRingOptionsTests
{
    [Theory] // regression risk: a PKCS#12 with an RSA private key loads, with a password or without
    [InlineData(null)]
    [InlineData("synthetic-unit-test-pfx-password")]
    public void LoadCertificate_ShouldLoadTheCertificateWithItsPrivateKey_WhenTheSettingIsAPkcs12(string? password)
    {
        var options = new KeyRingOptions { Certificate = SyntheticCertificate.Pkcs12Base64(password), CertificatePassword = password };

        options.Validate().Should().BeEmpty();
        using var certificate = options.LoadCertificate();
        certificate.HasPrivateKey.Should().BeTrue();
        using var key = certificate.GetRSAPrivateKey();
        key.Should().NotBeNull();
    }

    [Fact] // boundary: with sign-in on, the certificate is required: the default key ring is unencrypted (D-19)
    public void Validate_ShouldFailNamingTheSetting_WhenTheCertificateIsMissing()
    {
        new KeyRingOptions().Validate().Should().ContainSingle().Which.Should().Contain("DataProtection__Certificate");
    }

    [Theory] // boundary: a value that is not a base64 PKCS#12 stops the host
    [InlineData("not base64 at all")]
    [InlineData("AAAA")]
    public void Validate_ShouldFailNamingTheSetting_WhenTheCertificateDoesNotLoad(string value)
    {
        new KeyRingOptions { Certificate = value }.Validate()
            .Should().ContainSingle().Which.Should().Contain("DataProtection__Certificate");
    }

    [Fact] // boundary: the wrong password is a failure that names the password setting, not its value
    public void Validate_ShouldFailNamingThePassword_WhenThePasswordIsWrong()
    {
        var options = new KeyRingOptions
        {
            Certificate = SyntheticCertificate.Pkcs12Base64("synthetic-right-password"),
            CertificatePassword = "synthetic-wrong-password",
        };

        var failures = options.Validate().ToList();

        failures.Should().ContainSingle().Which.Should().Contain("DataProtection__CertificatePassword");
        failures.Should().NotContain(f => f.Contains("synthetic-wrong-password", StringComparison.Ordinal));
    }

    [Fact] // invariant: a certificate without its private key could encrypt keys it could never read back
    public void Validate_ShouldFail_WhenTheCertificateHasNoPrivateKey()
    {
        new KeyRingOptions { Certificate = SyntheticCertificate.Pkcs12Base64(withPrivateKey: false) }.Validate()
            .Should().ContainSingle().Which.Should().Contain("private key");
    }

    [Fact] // boundary: Data Protection encrypts its keys with RSA, so another key type is refused at startup
    public void Validate_ShouldFail_WhenTheCertificateIsNotRsa()
    {
        new KeyRingOptions { Certificate = SyntheticCertificate.EcdsaPkcs12Base64() }.Validate()
            .Should().ContainSingle().Which.Should().Contain("RSA");
    }

    [Fact] // boundary: a key too short to protect the key ring is refused
    public void Validate_ShouldFail_WhenTheRsaKeyIsShorterThan2048Bits()
    {
        new KeyRingOptions { Certificate = SyntheticCertificate.Pkcs12Base64(keySize: 1024) }.Validate()
            .Should().ContainSingle().Which.Should().Contain("2048");
    }

    [Fact] // regression risk: a loaded certificate decrypts what it encrypted (the key ring round trip depends on it)
    public void LoadCertificate_ShouldDecryptWhatItEncrypted_WhenLoaded()
    {
        using var certificate = new KeyRingOptions { Certificate = SyntheticCertificate.Pkcs12Base64() }.LoadCertificate();
        using var publicKey = certificate.GetRSAPublicKey()!;
        using var privateKey = certificate.GetRSAPrivateKey()!;

        var cipher = publicKey.Encrypt([1, 2, 3], RSAEncryptionPadding.OaepSHA256);

        privateKey.Decrypt(cipher, RSAEncryptionPadding.OaepSHA256).Should().Equal(1, 2, 3);
    }
}
