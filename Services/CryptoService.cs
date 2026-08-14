using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace p2p.Services;

public static class CryptoService
{
    public const int NonceSize = 12;
    public const int TagSize = 16;
    public const int SessionKeySize = 32;

    public static (string publicKey, string privateKey) GenerateSigningKeyPair()
    {
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return (
            Convert.ToBase64String(ecdsa.ExportSubjectPublicKeyInfo()),
            Convert.ToBase64String(ecdsa.ExportPkcs8PrivateKey())
        );
    }

    public static string Sign(string privateKeyBase64, byte[] data)
    {
        using var ecdsa = ECDsa.Create();
        ecdsa.ImportPkcs8PrivateKey(Convert.FromBase64String(privateKeyBase64), out _);
        return Convert.ToBase64String(ecdsa.SignData(data, HashAlgorithmName.SHA256));
    }

    public static bool Verify(string publicKeyBase64, byte[] data, string signatureBase64)
    {
        try
        {
            using var ecdsa = ECDsa.Create();
            ecdsa.ImportSubjectPublicKeyInfo(Convert.FromBase64String(publicKeyBase64), out _);
            return ecdsa.VerifyData(data, Convert.FromBase64String(signatureBase64), HashAlgorithmName.SHA256);
        }
        catch
        {
            return false;
        }
    }

    public static string ProtectPrivateKey(string privateKeyBase64)
    {
        var bytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(privateKeyBase64), null, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(bytes);
    }

    public static string UnprotectPrivateKey(string encryptedBase64)
    {
        var bytes = ProtectedData.Unprotect(Convert.FromBase64String(encryptedBase64), null, DataProtectionScope.CurrentUser);
        return Encoding.UTF8.GetString(bytes);
    }

    public static ECDiffieHellman GenerateEphemeral(out string publicKeyBase64)
    {
        var dh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        publicKeyBase64 = Convert.ToBase64String(dh.ExportSubjectPublicKeyInfo());
        return dh;
    }

    public static byte[] ComputeSharedSecret(ECDiffieHellman local, string remotePublicKeyBase64)
    {
        using var remote = ECDiffieHellman.Create();
        remote.ImportSubjectPublicKeyInfo(Convert.FromBase64String(remotePublicKeyBase64), out _);
        return local.DeriveKeyMaterial(remote.PublicKey);
    }

    public static byte[] DeriveSessionKey(byte[] sharedSecret, byte[] salt)
    {
        return HKDF.DeriveKey(HashAlgorithmName.SHA256, sharedSecret, SessionKeySize, salt, Encoding.UTF8.GetBytes("p2p-session-v1"));
    }

    public static byte[] Encrypt(byte[] plaintext, byte[] key)
    {
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[TagSize];
        using var aes = new AesGcm(key, TagSize);
        aes.Encrypt(nonce, plaintext, ciphertext, tag);

        var result = new byte[NonceSize + TagSize + ciphertext.Length];
        Buffer.BlockCopy(nonce, 0, result, 0, NonceSize);
        Buffer.BlockCopy(tag, 0, result, NonceSize, TagSize);
        Buffer.BlockCopy(ciphertext, 0, result, NonceSize + TagSize, ciphertext.Length);
        return result;
    }

    public static byte[] Decrypt(byte[] payload, byte[] key)
    {
        if (payload.Length < NonceSize + TagSize)
            throw new InvalidDataException("Invalid encrypted payload.");

        var nonce = payload[..NonceSize];
        var tag = payload[NonceSize..(NonceSize + TagSize)];
        var ciphertext = payload[(NonceSize + TagSize)..];

        var plaintext = new byte[ciphertext.Length];
        using var aes = new AesGcm(key, TagSize);
        aes.Decrypt(nonce, ciphertext, tag, plaintext);
        return plaintext;
    }

    public static string ComputeSha256Hex(string path)
    {
        using var sha = SHA256.Create();
        using var fs = File.OpenRead(path);
        return Convert.ToHexString(sha.ComputeHash(fs)).ToLowerInvariant();
    }
}
