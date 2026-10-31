using System.Security.Cryptography;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PclCmu.Admin;

/// <summary>
/// Signs the generated manifest with an ECDSA P-256 / SHA-256 key and returns a
/// detached JSON signature document. The signature is IEEE P1363 (r||s, 64
/// bytes) over the exact UTF-8 bytes of <c>manifest.json</c>.
/// </summary>
public sealed class ManifestSigner : IDisposable
{
    public const string Algorithm = "ECDSA-P256-SHA256";
    public const string KeyId = "cmu-manifest-2026-10";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        // Emit a clean base64 signature (no \u002B escaping of '+'); the .sig
        // is served as application/json, so HTML-safe escaping is unnecessary.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly ECDsa _ecdsa;

    /// <summary>
    /// Loads the EC private key from <paramref name="keyPath"/> and fails closed
    /// (throws) if the key is missing or not a valid EC key.
    /// </summary>
    public ManifestSigner(string keyPath)
    {
        if (string.IsNullOrWhiteSpace(keyPath))
            throw new InvalidOperationException("Manifest signing key path is not configured (ManifestSigningKeyPath).");

        if (!File.Exists(keyPath))
            throw new InvalidOperationException($"Manifest signing key not found: {keyPath}");

        ECDsa? ecdsa = null;
        try
        {
            ecdsa = ECDsa.Create();
            ecdsa.ImportFromPem(File.ReadAllText(keyPath));

            // P-256 has a 256-bit private key; reject anything else early.
            if (ecdsa.KeySize != 256)
                throw new InvalidOperationException($"Manifest signing key must be an ECDSA P-256 key (got {ecdsa.KeySize} bits): {keyPath}");

            _ecdsa = ecdsa;
            ecdsa = null; // ownership transferred
        }
        catch (Exception ex) when (ex is CryptographicException or ArgumentException or FormatException)
        {
            ecdsa?.Dispose();
            throw new InvalidOperationException($"Manifest signing key is invalid ({keyPath}): {ex.Message}", ex);
        }
        finally
        {
            ecdsa?.Dispose();
        }
    }

    /// <summary>Signs the exact manifest bytes and returns the JSON signature document.</summary>
    public string Sign(byte[] manifestBytes)
    {
        // ECDsa.SignData(data, hashAlgorithm) returns the IEEE P1363 r||s signature.
        var signature = _ecdsa.SignData(manifestBytes, HashAlgorithmName.SHA256);
        return JsonSerializer.Serialize(new SignatureDocument
        {
            Algorithm = Algorithm,
            KeyId = KeyId,
            Signature = Convert.ToBase64String(signature),
        }, JsonOptions);
    }

    public void Dispose() => _ecdsa.Dispose();
}

public sealed class SignatureDocument
{
    [JsonPropertyName("algorithm")] public string Algorithm { get; set; } = "";

    [JsonPropertyName("keyId")] public string KeyId { get; set; } = "";

    [JsonPropertyName("signature")] public string Signature { get; set; } = "";
}
