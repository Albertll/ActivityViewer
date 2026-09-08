using System.Security.Cryptography;
using GpxApi.Services;
using Microsoft.Extensions.Configuration;

namespace GpxApi.Tests;

public class EncryptionServiceTests
{
    private static EncryptionService CreateService(string? masterKey = null)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Encryption:MasterKey"] = masterKey ?? EncryptionService.GenerateMasterKey()
            })
            .Build();
        return new EncryptionService(config);
    }

    [Fact]
    public void Encrypt_Decrypt_Roundtrip_ZachowujePolskieZnaki()
    {
        var service = CreateService();
        const string plaintext = "Trening interwałowy: 5×4' żółć";

        var (data, iv) = service.Encrypt(plaintext, stravaAthleteId: 123);
        var decrypted = service.Decrypt(data, iv, stravaAthleteId: 123);

        Assert.Equal(plaintext, decrypted);
    }

    [Fact]
    public void Decrypt_InnymAthleteId_NieZwracaOryginalu()
    {
        var service = CreateService();
        const string plaintext = "sekretne dane";
        var (data, iv) = service.Encrypt(plaintext, stravaAthleteId: 123);

        try
        {
            var result = service.Decrypt(data, iv, stravaAthleteId: 999);
            Assert.NotEqual(plaintext, result); // pechowo poprawny padding - wynik i tak musi być śmieciem
        }
        catch (CryptographicException)
        {
            // oczekiwane: zły klucz zwykle psuje padding PKCS7
        }
    }

    [Fact]
    public void EncryptToken_DecryptToken_Roundtrip()
    {
        var service = CreateService();
        const string token = "abc123-refresh-token";

        var decrypted = service.DecryptToken(service.EncryptToken(token));

        Assert.Equal(token, decrypted);
    }

    [Fact]
    public void GenerateMasterKey_Zwraca32BajtyBase64()
    {
        var key = Convert.FromBase64String(EncryptionService.GenerateMasterKey());
        Assert.Equal(32, key.Length);
    }

    [Fact]
    public void Konstruktor_ZaKrotkiKlucz_RzucaWyjatkiem()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Encryption:MasterKey"] = Convert.ToBase64String(new byte[16])
            })
            .Build();

        Assert.Throws<InvalidOperationException>(() => new EncryptionService(config));
    }
}
