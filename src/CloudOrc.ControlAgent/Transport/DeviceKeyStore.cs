using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace CloudOrc.ControlAgent.Transport;

/// <summary>
/// The agent's device identity key (§4 "machine identity proof", §10 "Device mTLS identity plus
/// short-lived session credentials"). An ECDSA P-256 key in the Windows machine key store,
/// created NON-EXPORTABLE: the private key never leaves CNG, is never written to agent files or
/// logs, and does not survive a reimage (which correctly forces re-activation).
///
/// Used to (1) sign session-renewal proofs and (2) generate the CSR for an mTLS client
/// certificate when the backend issues one.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class DeviceKeyStore(string keyName = DeviceKeyStore.DefaultKeyName)
{
    public const string DefaultKeyName = "CloudOrc.Agent.DeviceKey.v1";

    private static readonly CngProvider Provider = CngProvider.MicrosoftSoftwareKeyStorageProvider;

    public bool Exists() => CngKey.Exists(keyName, Provider, CngKeyOpenOptions.MachineKey);

    /// <summary>Opens the key, creating it (non-exportable) on first use.</summary>
    public ECDsaCng OpenOrCreate()
    {
        CngKey key;
        if (Exists())
        {
            key = CngKey.Open(keyName, Provider, CngKeyOpenOptions.MachineKey);
        }
        else
        {
            key = CngKey.Create(CngAlgorithm.ECDsaP256, keyName, new CngKeyCreationParameters
            {
                Provider = Provider,
                KeyCreationOptions = CngKeyCreationOptions.MachineKey,
                ExportPolicy = CngExportPolicies.None,
                KeyUsage = CngKeyUsages.Signing
            });
        }

        return new ECDsaCng(key);
    }

    /// <summary>Base64 SubjectPublicKeyInfo of the device key (safe to send/store anywhere).</summary>
    public string GetPublicKeySpki()
    {
        using var key = OpenOrCreate();
        return Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());
    }

    /// <summary>DER (RFC 3279) ECDSA-SHA256 signature - the format Node's crypto.verify and OpenSSL expect.</summary>
    public string Sign(string payload)
    {
        using var key = OpenOrCreate();
        var signature = key.SignData(Encoding.UTF8.GetBytes(payload), HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
        return Convert.ToBase64String(signature);
    }

    /// <summary>PKCS#10 CSR for an mTLS client certificate bound to the device key.</summary>
    public string CreateCsrPem(string subjectCommonName)
    {
        using var key = OpenOrCreate();
        var request = new CertificateRequest(new X500DistinguishedName($"CN={Sanitize(subjectCommonName)}"), key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, critical: true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.2")], critical: false)); // clientAuth
        return request.CreateSigningRequestPem();
    }

    /// <summary>
    /// Binds a backend-issued client certificate to the device key and stores it in
    /// LocalMachine\My. Returns its thumbprint (persisted in the enrolled state).
    /// </summary>
    public string InstallClientCertificate(string certificatePem)
    {
        using var publicOnly = X509Certificate2.CreateFromPem(certificatePem);
        using var key = OpenOrCreate();
        using var withKey = publicOnly.CopyWithPrivateKey(key);

        using var store = new X509Store(StoreName.My, StoreLocation.LocalMachine);
        store.Open(OpenFlags.ReadWrite);
        store.Add(withKey);
        return withKey.Thumbprint;
    }

    public static X509Certificate2? FindClientCertificate(string? thumbprint)
    {
        if (string.IsNullOrWhiteSpace(thumbprint))
        {
            return null;
        }

        using var store = new X509Store(StoreName.My, StoreLocation.LocalMachine);
        store.Open(OpenFlags.ReadOnly);
        var matches = store.Certificates.Find(X509FindType.FindByThumbprint, thumbprint, validOnly: false);
        var cert = matches.Count > 0 ? matches[0] : null;
        return cert is { HasPrivateKey: true } && cert.NotAfter > DateTime.Now ? cert : null;
    }

    private static string Sanitize(string value)
    {
        var chars = value.Where(c => char.IsLetterOrDigit(c) || c is '-' or '.' or '_').ToArray();
        return chars.Length == 0 ? "cloudorc-agent" : new string(chars);
    }
}
