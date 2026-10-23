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

    public static bool SigningAvailable => false;

    public static void SignFile(string path)
    {
        if (!SigningAvailable) throw new NotSupportedException("Xbox 360 profile signing is unavailable in this source build. No profile was modified.");
        using (var stream = File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var header = ReadHeader(stream);
            VerifyContentId(stream, header);
            var certificate = Xbox360ConSigningMaterial.Certificate;
            if (certificate.Length != CertificateLength || Xbox360ConSigningMaterial.PrimeP.Length != 64 ||
                Xbox360ConSigningMaterial.PrimeQ.Length != 64)
                throw new InvalidDataException("The CON signing material is incomplete.");

            var modulus = GetModulus(certificate);
            var exponent = new BigInteger(certificate.AsSpan(0x24, 4), isUnsigned: true, isBigEndian: true);
            var primeP = new BigInteger(Xbox360ConSigningMaterial.PrimeP, isUnsigned: true, isBigEndian: true);
            var primeQ = new BigInteger(Xbox360ConSigningMaterial.PrimeQ, isUnsigned: true, isBigEndian: true);
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
