using System.Numerics;
using System.Security.Cryptography;

namespace AchievementLabs.Services.Xbox360;

/// <summary>Signs and verifies the CON header of a locally staged Xbox 360 package.</summary>
public static class Xbox360ConSigner
{
    private const int HeaderLength = 0x344;
    private const int SignatureLength = 0x80;
    private const int CertificateLength = 0x1A8;
    private static readonly byte[] Sha1DigestInfo = Convert.FromHexString("3021300906052B0E03021A05000414");

    public static bool SigningAvailable => Xbox360ConSigningMaterial.Available;

    internal static void ValidateSigningMaterial(byte[] certificate, byte[] p, byte[] q)
    {
        if (certificate.Length != CertificateLength || p.Length != 64 || q.Length != 64 || certificate[0] != 1 || certificate[1] != 0xA8)
            throw new InvalidDataException("The signing material has an unsupported layout.");
        var exponent = new BigInteger(certificate.AsSpan(0x24, 4), true, true);
        var primeP = new BigInteger(p, true, true); var primeQ = new BigInteger(q, true, true);
        var modulus = GetModulus(certificate);
        if (exponent != 65537 || primeP <= 2 || primeQ <= 2 || primeP * primeQ != modulus)
            throw new InvalidDataException("The local signing key does not match its console certificate.");
        var privateExponent = ModInverse(exponent, (primeP - 1) * (primeQ - 1));
        if (BigInteger.ModPow(BigInteger.ModPow(2, privateExponent, modulus), exponent, modulus) != 2)
            throw new InvalidDataException("The local signing key failed its consistency test.");
    }

    public static void SignFile(string path)
    {
        if (!SigningAvailable) throw new NotSupportedException("Xbox 360 profile signing is unavailable in this source build. No profile was modified.");
        using (var stream = File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var header = ReadHeader(stream);
            VerifyContentId(stream, header);
            var material = Xbox360ConSigningMaterial.Snapshot();
            try
            {
            var certificate = material.Certificate;
            ValidateSigningMaterial(certificate, material.P, material.Q);

            var modulus = GetModulus(certificate);
            var exponent = new BigInteger(certificate.AsSpan(0x24, 4), isUnsigned: true, isBigEndian: true);
            var primeP = new BigInteger(material.P, isUnsigned: true, isBigEndian: true);
            var primeQ = new BigInteger(material.Q, isUnsigned: true, isBigEndian: true);
            if (exponent != 65537 || primeP * primeQ != modulus)
                throw new InvalidDataException("The CON signing material does not match its certificate.");

            var privateExponent = ModInverse(exponent, (primeP - 1) * (primeQ - 1));
            var digest = SHA1.HashData(header.AsSpan(0x22C, 0x118));
            var message = new BigInteger(EncodePkcs1Sha1(digest), isUnsigned: true, isBigEndian: true);
            var signature = ToFixedBytes(BigInteger.ModPow(message, privateExponent, modulus), SignatureLength);
            Array.Reverse(signature);

            stream.Position = 0x004;
            stream.Write(certificate);
            stream.Write(signature);
            stream.Flush(true);
            }
            finally { CryptographicOperations.ZeroMemory(material.P); CryptographicOperations.ZeroMemory(material.Q); }
        }
        if (!VerifyFile(path)) throw new IOException("The signed CON header failed verification.");
    }

    public static bool VerifyFile(string path)
    {
        using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var header = ReadHeader(stream);
        try { VerifyContentId(stream, header); }
        catch (InvalidDataException) { return false; }
        var certificate = header.AsSpan(0x004, CertificateLength);
        if (certificate[0] != 0x01 || certificate[1] != 0xA8) return false;
        var modulus = GetModulus(certificate);
        var exponent = new BigInteger(certificate.Slice(0x24, 4), isUnsigned: true, isBigEndian: true);
        if (modulus <= 0 || exponent <= 0) return false;
        var storedSignature = header.AsSpan(0x1AC, SignatureLength).ToArray();
        Array.Reverse(storedSignature);
        var signature = new BigInteger(storedSignature, isUnsigned: true, isBigEndian: true);
        var decoded = ToFixedBytes(BigInteger.ModPow(signature, exponent, modulus), SignatureLength);
        var expected = EncodePkcs1Sha1(SHA1.HashData(header.AsSpan(0x22C, 0x118)));
        return CryptographicOperations.FixedTimeEquals(decoded, expected);
    }

    private static byte[] ReadHeader(Stream stream)
    {
        if (stream.Length < 0xA000) throw new InvalidDataException("The CON package header is incomplete.");
        var header = new byte[HeaderLength];
        stream.Position = 0;
        stream.ReadExactly(header);
        if (header[0] != 'C' || header[1] != 'O' || header[2] != 'N' || header[3] != ' ')
            throw new InvalidDataException("Expected a CON package.");
        return header;
    }

    private static void VerifyContentId(Stream stream, ReadOnlySpan<byte> header)
    {
        var data = new byte[0xA000 - HeaderLength];
        stream.Position = HeaderLength;
        stream.ReadExactly(data);
        if (!SHA1.HashData(data).AsSpan().SequenceEqual(header.Slice(0x32C, 20)))
            throw new InvalidDataException("The CON metadata hash is invalid.");
    }

    private static BigInteger GetModulus(ReadOnlySpan<byte> certificate)
    {
        var stored = certificate.Slice(0x28, SignatureLength);
        var normalized = new byte[SignatureLength];
        for (var index = 0; index < SignatureLength; index += 8)
            stored.Slice(SignatureLength - index - 8, 8).CopyTo(normalized.AsSpan(index, 8));
        return new BigInteger(normalized, isUnsigned: true, isBigEndian: true);
    }

    private static byte[] EncodePkcs1Sha1(ReadOnlySpan<byte> digest)
    {
        if (digest.Length != 20) throw new ArgumentException("Expected a SHA-1 digest.", nameof(digest));
        var encoded = new byte[SignatureLength];
        encoded[1] = 0x01;
        encoded.AsSpan(2, 90).Fill(0xFF);
        Sha1DigestInfo.AsSpan().CopyTo(encoded.AsSpan(93));
        digest.CopyTo(encoded.AsSpan(108));
        return encoded;
    }

    private static BigInteger ModInverse(BigInteger value, BigInteger modulus)
    {
        var previous = BigInteger.Zero;
        var current = BigInteger.One;
        var remainder = modulus;
        var nextRemainder = value;
        while (nextRemainder != 0)
        {
            var quotient = remainder / nextRemainder;
            (previous, current) = (current, previous - quotient * current);
            (remainder, nextRemainder) = (nextRemainder, remainder - quotient * nextRemainder);
        }
        if (remainder != 1) throw new InvalidDataException("The CON signing exponent is not invertible.");
        return (previous % modulus + modulus) % modulus;
    }

    private static byte[] ToFixedBytes(BigInteger value, int length)
    {
        var bytes = value.ToByteArray(isUnsigned: true, isBigEndian: true);
        if (bytes.Length > length) throw new InvalidDataException("The CON signature has an invalid length.");
        var result = new byte[length];
        bytes.AsSpan().CopyTo(result.AsSpan(length - bytes.Length));
        return result;
    }
}
