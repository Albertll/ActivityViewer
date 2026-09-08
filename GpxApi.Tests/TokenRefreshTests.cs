using System.Net;
using GpxApi.Data;
using GpxApi.Models;
using GpxApi.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace GpxApi.Tests;

public class TokenRefreshTests
{
    private static readonly string MasterKey = EncryptionService.GenerateMasterKey();

    private static (StravaActivitySyncService Service, AppDbContext Db, EncryptionService Encryption, FakeHttpClientFactory Http) Build(
        Func<HttpRequestMessage, HttpResponseMessage> responder)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Encryption:MasterKey"] = MasterKey,
                ["Strava:ClientId"] = "111",
                ["Strava:ClientSecret"] = "sekret"
            })
            .Build();

        var encryption = new EncryptionService(config);
        var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);
        var http = new FakeHttpClientFactory(responder);
        var provider = new ServiceCollection().BuildServiceProvider();
        var service = new StravaActivitySyncService(
            http,
            NullLogger<StravaActivitySyncService>.Instance,
            provider.GetRequiredService<IServiceScopeFactory>(),
            config);

        return (service, db, encryption, http);
    }

    private static AppUser NewUser(EncryptionService encryption, long expiresAt) => new()
    {
        StravaAthleteId = 42,
        EncryptedAccessToken = encryption.EncryptToken("stary-access"),
        EncryptedRefreshToken = encryption.EncryptToken("refresh-abc"),
        TokenExpiresAt = expiresAt
    };

    [Fact]
    public async Task WaznyToken_ZwracanyBezOdswiezania()
    {
        var (service, db, encryption, http) = Build(_ => throw new InvalidOperationException("nie powinno być żadnego requestu"));
        var user = NewUser(encryption, DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 3600);
        db.Users.Add(user);
        await db.SaveChangesAsync();

        var token = await service.GetValidAccessTokenAsync(db, user, encryption, CancellationToken.None);

        Assert.Equal("stary-access", token);
        Assert.Equal(0, http.RequestCount);
    }

    [Fact]
    public async Task WygaslyToken_OdswiezonyIZapisanyWBazie()
    {
        var newExpiry = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 21600;
        var (service, db, encryption, http) = Build(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                $$"""{"access_token":"nowy-access","refresh_token":"nowy-refresh","expires_at":{{newExpiry}}}""")
        });
        var user = NewUser(encryption, DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 100);
        db.Users.Add(user);
        await db.SaveChangesAsync();

        var token = await service.GetValidAccessTokenAsync(db, user, encryption, CancellationToken.None);

        Assert.Equal("nowy-access", token);
        Assert.Equal(1, http.RequestCount);
        Assert.Equal(newExpiry, user.TokenExpiresAt);
        Assert.Equal("nowy-access", encryption.DecryptToken(user.EncryptedAccessToken!));
        Assert.Equal("nowy-refresh", encryption.DecryptToken(user.EncryptedRefreshToken!));
    }

    [Fact]
    public async Task NieudaneOdswiezenie_ZwracaNull()
    {
        var (service, db, encryption, http) = Build(_ => new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("{}")
        });
        var user = NewUser(encryption, DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 100);
        db.Users.Add(user);
        await db.SaveChangesAsync();

        var token = await service.GetValidAccessTokenAsync(db, user, encryption, CancellationToken.None);

        Assert.Null(token);
    }

    [Fact]
    public async Task BrakTokenow_ZwracaNullBezRequestow()
    {
        var (service, db, encryption, http) = Build(_ => throw new InvalidOperationException("nie powinno być żadnego requestu"));
        var user = new AppUser { StravaAthleteId = 42 };
        db.Users.Add(user);
        await db.SaveChangesAsync();

        var token = await service.GetValidAccessTokenAsync(db, user, encryption, CancellationToken.None);

        Assert.Null(token);
        Assert.Equal(0, http.RequestCount);
    }
}
