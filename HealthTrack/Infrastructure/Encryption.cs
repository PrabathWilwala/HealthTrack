using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace HealthTrack.Infrastructure;

// Marks a string property as sensitive health data. The DbContext encrypts such columns
// at rest using ASP.NET Core Data Protection (AES-256-CBC + HMACSHA256 by default).
[AttributeUsage(AttributeTargets.Property)]
public sealed class EncryptedAttribute : Attribute
{
}

// EF Core value converter that encrypts a string before it is written to the database
// and decrypts it when it is read back.
public sealed class EncryptedStringConverter : ValueConverter<string, string>
{
    // Prefix that identifies encrypted values (lets us tolerate legacy plain text).
    private const string Prefix = "enc:";

    public EncryptedStringConverter(IDataProtector protector)
        : base(value => Encrypt(protector, value), value => Decrypt(protector, value))
    {
    }

    private static string Encrypt(IDataProtector protector, string plainText)
    {
        return Prefix + protector.Protect(plainText);
    }

    private static string Decrypt(IDataProtector protector, string storedValue)
    {
        if (!storedValue.StartsWith(Prefix, StringComparison.Ordinal))
            return storedValue; // value was stored before encryption was enabled

        try
        {
            return protector.Unprotect(storedValue.Substring(Prefix.Length));
        }
        catch (CryptographicException)
        {
            // The key ring was lost or changed. Never crash the page because of it.
            return "[This note could not be decrypted]";
        }
    }
}
