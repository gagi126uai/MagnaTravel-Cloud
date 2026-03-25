using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TravelApi.Application.Interfaces;

namespace TravelApi.Infrastructure.Services;

public class SensitiveDataProtector : ISensitiveDataProtector
{
    private static readonly byte[] BinaryMarker = Encoding.ASCII.GetBytes("MTENC1");
    private readonly byte[]? _key;
    private readonly bool _noopMode;

    public SensitiveDataProtector(
        IConfiguration configuration,
        IHostEnvironment environment,
        ILogger<SensitiveDataProtector> logger)
    {
        var configuredKey = configuration["Security:EncryptionKey"] ?? configuration["Security__EncryptionKey"];
        if (string.IsNullOrWhiteSpace(configuredKey))
        {
            if (!environment.IsDevelopment())
            {
                throw new InvalidOperationException("Security encryption key is required outside development.");
            }

            _noopMode = true;
            logger.LogWarning("Security encryption key is not configured. Sensitive data protection is running in noop mode.");
            return;
        }

        _key = DeriveKey(configuredKey);
    }

    public string? ProtectString(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return value;
        }

        if (_noopMode || value.StartsWith("enc:", StringComparison.Ordinal))
        {
            return value;
        }

        var cipherBytes = ProtectBytesCore(Encoding.UTF8.GetBytes(value));
        return $"enc:{Convert.ToBase64String(cipherBytes)}";
    }

    public string? UnprotectString(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || _noopMode || !value.StartsWith("enc:", StringComparison.Ordinal))
        {
            return value;
        }

        var cipherBytes = Convert.FromBase64String(value["enc:".Length..]);
        var plainBytes = UnprotectBytesCore(cipherBytes);
        return Encoding.UTF8.GetString(plainBytes);
    }

    public byte[]? ProtectBytes(byte[]? value)
    {
        if (value is null || value.Length == 0 || _noopMode || HasMarker(value))
        {
            return value;
        }

        return ProtectBytesCore(value);
    }

    public byte[]? UnprotectBytes(byte[]? value)
    {
        if (value is null || value.Length == 0 || _noopMode || !HasMarker(value))
        {
            return value;
        }

        return UnprotectBytesCore(value);
    }

    private static byte[] DeriveKey(string configuredKey)
    {
        try
        {
            var decoded = Convert.FromBase64String(configuredKey);
            if (decoded.Length >= 32)
            {
                return decoded[..32];
            }
        }
        catch
        {
            // Fall through to deterministic derivation from plain text.
        }

        return SHA256.HashData(Encoding.UTF8.GetBytes(configuredKey));
    }

    private static bool HasMarker(byte[] value)
    {
        return value.Length > BinaryMarker.Length &&
               value.AsSpan(0, BinaryMarker.Length).SequenceEqual(BinaryMarker);
    }

    private byte[] ProtectBytesCore(byte[] plainBytes)
    {
        var nonce = RandomNumberGenerator.GetBytes(AesGcm.NonceByteSizes.MaxSize);
        var cipherBytes = new byte[plainBytes.Length];
        var tag = new byte[AesGcm.TagByteSizes.MaxSize];

        using var aes = new AesGcm(_key!, AesGcm.TagByteSizes.MaxSize);
        aes.Encrypt(nonce, plainBytes, cipherBytes, tag);

        var output = new byte[BinaryMarker.Length + nonce.Length + tag.Length + cipherBytes.Length];
        Buffer.BlockCopy(BinaryMarker, 0, output, 0, BinaryMarker.Length);
        Buffer.BlockCopy(nonce, 0, output, BinaryMarker.Length, nonce.Length);
        Buffer.BlockCopy(tag, 0, output, BinaryMarker.Length + nonce.Length, tag.Length);
        Buffer.BlockCopy(cipherBytes, 0, output, BinaryMarker.Length + nonce.Length + tag.Length, cipherBytes.Length);
        return output;
    }

    private byte[] UnprotectBytesCore(byte[] cipherPayload)
    {
        var nonceOffset = BinaryMarker.Length;
        var tagOffset = nonceOffset + AesGcm.NonceByteSizes.MaxSize;
        var cipherOffset = tagOffset + AesGcm.TagByteSizes.MaxSize;

        var nonce = cipherPayload.AsSpan(nonceOffset, AesGcm.NonceByteSizes.MaxSize).ToArray();
        var tag = cipherPayload.AsSpan(tagOffset, AesGcm.TagByteSizes.MaxSize).ToArray();
        var cipherBytes = cipherPayload.AsSpan(cipherOffset).ToArray();
        var plainBytes = new byte[cipherBytes.Length];

        using var aes = new AesGcm(_key!, AesGcm.TagByteSizes.MaxSize);
        aes.Decrypt(nonce, cipherBytes, tag, plainBytes);
        return plainBytes;
    }
}
