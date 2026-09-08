using System.Security.Cryptography;
using System.Text;

namespace GpxApi.Services;

/// <summary>
/// Szyfrowanie AES-256-CBC z kluczem per-user wyprowadzonym z MasterKey + StravaAthleteId.
/// Nie wymaga hasła od użytkownika - klucz jest generowany automatycznie.
/// </summary>
public class EncryptionService
{
    private readonly byte[] _masterKey;

    public EncryptionService(IConfiguration configuration)
    {
        var masterKeyBase64 = configuration["Encryption:MasterKey"]
            ?? throw new InvalidOperationException("Brak klucza Encryption:MasterKey w konfiguracji");

        _masterKey = Convert.FromBase64String(masterKeyBase64);

        if (_masterKey.Length < 32)
            throw new InvalidOperationException("MasterKey musi mieć minimum 32 bajty (256 bitów)");
    }

    /// <summary>
    /// Wyprowadza unikalny klucz AES-256 dla użytkownika z MasterKey + StravaAthleteId
    /// </summary>
    private byte[] DeriveUserKey(long stravaAthleteId)
    {
        var athleteBytes = Encoding.UTF8.GetBytes(stravaAthleteId.ToString());
        return HMACSHA256.HashData(_masterKey, athleteBytes);
    }

    /// <summary>
    /// Szyfruje dane tekstowe (np. JSON streamu)
    /// </summary>
    public (byte[] encryptedData, byte[] iv) Encrypt(string plainText, long stravaAthleteId)
    {
        var key = DeriveUserKey(stravaAthleteId);
        using var aes = Aes.Create();
        aes.Key = key;
        aes.GenerateIV();
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;

        using var encryptor = aes.CreateEncryptor();
        var plainBytes = Encoding.UTF8.GetBytes(plainText);
        var encrypted = encryptor.TransformFinalBlock(plainBytes, 0, plainBytes.Length);

        return (encrypted, aes.IV);
    }

    /// <summary>
    /// Deszyfruje dane
    /// </summary>
    public string Decrypt(byte[] encryptedData, byte[] iv, long stravaAthleteId)
    {
        var key = DeriveUserKey(stravaAthleteId);
        using var aes = Aes.Create();
        aes.Key = key;
        aes.IV = iv;
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;

        using var decryptor = aes.CreateDecryptor();
        var decrypted = decryptor.TransformFinalBlock(encryptedData, 0, encryptedData.Length);

        return Encoding.UTF8.GetString(decrypted);
    }

    /// <summary>
    /// Szyfruje token (access/refresh) przy użyciu MasterKey bezpośrednio
    /// </summary>
    public string EncryptToken(string token)
    {
        using var aes = Aes.Create();
        aes.Key = _masterKey[..32];
        aes.GenerateIV();
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;

        using var encryptor = aes.CreateEncryptor();
        var plainBytes = Encoding.UTF8.GetBytes(token);
        var encrypted = encryptor.TransformFinalBlock(plainBytes, 0, plainBytes.Length);

        // Zwróć IV + encrypted jako Base64
        var combined = new byte[aes.IV.Length + encrypted.Length];
        aes.IV.CopyTo(combined, 0);
        encrypted.CopyTo(combined, aes.IV.Length);

        return Convert.ToBase64String(combined);
    }

    /// <summary>
    /// Deszyfruje token
    /// </summary>
    public string DecryptToken(string encryptedBase64)
    {
        var combined = Convert.FromBase64String(encryptedBase64);

        using var aes = Aes.Create();
        aes.Key = _masterKey[..32];
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;

        var iv = combined[..16];
        var encrypted = combined[16..];
        aes.IV = iv;

        using var decryptor = aes.CreateDecryptor();
        var decrypted = decryptor.TransformFinalBlock(encrypted, 0, encrypted.Length);

        return Encoding.UTF8.GetString(decrypted);
    }

    /// <summary>
    /// Generuje losowy MasterKey (do użycia przy pierwszej konfiguracji)
    /// </summary>
    public static string GenerateMasterKey()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        return Convert.ToBase64String(key);
    }
}
