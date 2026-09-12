using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;

namespace JadeAudioControl.Compat;

/// <summary>
/// Loads an RSA public key from PEM.
///
/// RSA.ImportFromPem is .NET Core 3.0 and up, so on .NET Framework the
/// SubjectPublicKeyInfo has to be unpicked by hand:
///
///   SEQUENCE {
///     SEQUENCE { OID rsaEncryption, NULL }
///     BIT STRING { SEQUENCE { INTEGER modulus, INTEGER exponent } }
///   }
/// </summary>
internal static class Pem
{
    public static RSA LoadPublicKey(string pem)
    {
        var der = DecodeBase64Body(pem);
        var reader = new DerReader(der);

        reader.ReadSequenceHeader();          // outer SubjectPublicKeyInfo
        reader.SkipAlgorithmIdentifier();     // SEQUENCE { OID, NULL }
        var keyBits = reader.ReadBitString(); // the RSAPublicKey inside

        var key = new DerReader(keyBits);
        key.ReadSequenceHeader();
        byte[] modulus = TrimLeadingZero(key.ReadInteger());
        byte[] exponent = TrimLeadingZero(key.ReadInteger());

        var rsa = new RSACryptoServiceProvider();
        rsa.ImportParameters(new RSAParameters { Modulus = modulus, Exponent = exponent });
        return rsa;
    }

    private static byte[] DecodeBase64Body(string pem)
    {
        var body = new StringBuilder();
        foreach (string raw in pem.Split('\n'))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("-----", StringComparison.Ordinal))
                continue;
            body.Append(line);
        }
        return Convert.FromBase64String(body.ToString());
    }

    /// <summary>DER integers are signed, so a leading 0x00 pad has to come off.</summary>
    private static byte[] TrimLeadingZero(byte[] value)
    {
        if (value.Length > 1 && value[0] == 0x00)
        {
            var trimmed = new byte[value.Length - 1];
            Array.Copy(value, 1, trimmed, 0, trimmed.Length);
            return trimmed;
        }
        return value;
    }

    private sealed class DerReader
    {
        private readonly byte[] _data;
        private int _position;

        public DerReader(byte[] data) => _data = data;

        private byte ReadByte() => _data[_position++];

        private int ReadLength()
        {
            int first = ReadByte();
            if ((first & 0x80) == 0)
                return first;

            int count = first & 0x7F;
            if (count is 0 or > 4)
                throw new CryptographicException("Unsupported DER length.");

            int length = 0;
            for (int i = 0; i < count; i++)
                length = (length << 8) | ReadByte();
            return length;
        }

        private void Expect(byte tag)
        {
            byte actual = ReadByte();
            if (actual != tag)
                throw new CryptographicException($"Expected DER tag 0x{tag:X2}, found 0x{actual:X2}.");
        }

        private byte[] Take(int count)
        {
            var slice = new byte[count];
            Array.Copy(_data, _position, slice, 0, count);
            _position += count;
            return slice;
        }

        public void ReadSequenceHeader()
        {
            Expect(0x30);
            ReadLength();
        }

        public void SkipAlgorithmIdentifier()
        {
            Expect(0x30);
            int length = ReadLength();
            _position += length;
        }

        public byte[] ReadBitString()
        {
            Expect(0x03);
            int length = ReadLength();
            ReadByte(); // unused-bits count, always 0 for a key
            return Take(length - 1);
        }

        public byte[] ReadInteger()
        {
            Expect(0x02);
            return Take(ReadLength());
        }
    }
}
