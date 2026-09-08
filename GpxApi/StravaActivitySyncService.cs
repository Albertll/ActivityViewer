using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using GpxApi.Data;
using GpxApi.Models;
using GpxApi.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

public class StravaActivitySyncService : BackgroundService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<StravaActivitySyncService> _logger;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IConfiguration _configuration;
    private readonly ConcurrentQueue<SyncRequest> _syncQueue = new();
    private readonly SemaphoreSlim _signal = new(0);
    private readonly SemaphoreSlim _refreshLock = new(1, 1);

    public record SyncRequest(int UserId, long StravaAthleteId, bool IsFirstLogin, TaskCompletionSource<int>? Completion = null);

    public StravaActivitySyncService(
        IHttpClientFactory httpClientFactory,
        ILogger<StravaActivitySyncService> logger,
        IServiceScopeFactory scopeFactory,
        IConfiguration configuration)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
        _scopeFactory = scopeFactory;
        _configuration = configuration;
    }

    public void TriggerSync(int userId, long stravaAthleteId, bool isFirstLogin)
    {
        _syncQueue.Enqueue(new SyncRequest(userId, stravaAthleteId, isFirstLogin));
        _signal.Release();
    }

    /// <summary>
    /// Jak TriggerSync, ale zwraca Task kończący się liczbą nowo dodanych aktywności -
    /// pozwala kontrolerowi poczekać na wynik synchronizacji.
    /// </summary>
    public Task<int> TriggerSyncAndWaitAsync(int userId, long stravaAthleteId, bool isFirstLogin)
    {
        var completion = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        _syncQueue.Enqueue(new SyncRequest(userId, stravaAthleteId, isFirstLogin, completion));
        _signal.Release();
        return completion.Task;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Uruchom timer do periodycznego pobierania streamów i GPX co 15 minut
        _ = PeriodicStreamGpxSync(stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            await _signal.WaitAsync(stoppingToken);

            while (_syncQueue.TryDequeue(out var request))
            {
                try
                {
                    _logger.LogInformation("[StravaSync] Start sync for userId={UserId} athleteId={AthleteId} firstLogin={First}",
                        request.UserId, request.StravaAthleteId, request.IsFirstLogin);
                    var added = await SyncActivityList(request, stoppingToken);
                    request.Completion?.TrySetResult(added);
                    _logger.LogInformation("[StravaSync] Sync finished for userId={UserId}", request.UserId);
                }
                catch (Exception ex)
                {
                    request.Completion?.TrySetException(ex);
                    _logger.LogError(ex, "Błąd synchronizacji aktywności Strava dla userId={UserId}", request.UserId);
                }
            }
        }
    }

    /// <summary>
    /// Synchronizuje listę aktywności (metadane) ze Stravy.
    /// Pierwsze logowanie: pobiera WSZYSTKIE strony.
    /// Kolejne logowania: pobiera tylko stronę 1 i dodaje nowe.
    /// Nie pobiera streamów ani GPX - to robi PeriodicStreamGpxSync lub on-demand.
    /// Zwraca liczbę nowo dodanych aktywności.
    /// </summary>
    private async Task<int> SyncActivityList(SyncRequest request, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var encryption = scope.ServiceProvider.GetRequiredService<EncryptionService>();

        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == request.UserId, ct);
        var accessToken = user == null ? null : await GetValidAccessTokenAsync(db, user, encryption, ct);
        if (user == null || accessToken == null)
        {
            _logger.LogWarning("[StravaSync] Brak ważnego tokenu Strava dla userId={UserId} - pomijam sync listy", request.UserId);
            return 0;
        }

        var client = _httpClientFactory.CreateClient();
        int page = 1;
        int perPage = 200;
        int newCount = 0;

        var existingIds = await db.Activities
            .Where(a => a.UserId == request.UserId)
            .Select(a => a.StravaActivityId)
            .ToHashSetAsync(ct);

        bool fetchAllPages = request.IsFirstLogin;

        while (!ct.IsCancellationRequested)
        {
            var url = $"https://www.strava.com/api/v3/athlete/activities?per_page={perPage}&page={page}";
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

            var resp = await client.GetAsync(url, ct);
            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogWarning("[StravaSync] Strava API zwróciło {Code} na stronie {Page}", resp.StatusCode, page);
                break;
            }

            var json = await resp.Content.ReadAsStringAsync(ct);
            var activities = JsonDocument.Parse(json).RootElement;
            int pageCount = activities.GetArrayLength();
            bool foundExisting = false;

            foreach (var act in activities.EnumerateArray())
            {
                var id = act.GetProperty("id").GetInt64();
                if (existingIds.Contains(id))
                {
                    foundExisting = true;
                    if (!fetchAllPages) break; // Przy kolejnych logowaniach - jak trafiliśmy na istniejącą, koniec
                    continue;
                }

                var actRecord = new ActivityRecord
                {
                    UserId = request.UserId,
                    StravaActivityId = id,
                    Name = act.TryGetProperty("name", out var n) ? n.GetString() : null,
                    Type = act.TryGetProperty("type", out var t) ? t.GetString() : null,
                    Distance = act.TryGetProperty("distance", out var d) ? d.GetDouble() : 0,
                    MovingTime = act.TryGetProperty("moving_time", out var mt) ? mt.GetInt32() : 0,
                    StartDate = act.TryGetProperty("start_date", out var sd)
                        ? DateTime.Parse(sd.GetString()!, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal)
                        : null,
                    TotalElevationGain = act.TryGetProperty("total_elevation_gain", out var eg) ? eg.GetDouble() : 0,
                    AverageSpeed = act.TryGetProperty("average_speed", out var avgSp) ? avgSp.GetDouble() : (double?)null,
                    MaxSpeed = act.TryGetProperty("max_speed", out var maxSp) ? maxSp.GetDouble() : (double?)null
                };

                db.Activities.Add(actRecord);
                existingIds.Add(id);
                newCount++;
            }

            // Zapisuj po każdej stronie
            if (newCount > 0)
                await db.SaveChangesAsync(ct);

            // Przy kolejnym logowaniu: zatrzymaj się jeśli trafiliśmy na istniejącą
            if (!fetchAllPages && foundExisting)
                break;

            // Koniec stron
            if (pageCount < perPage)
                break;

            page++;
        }

        // Oznacz pierwszą synchronizację jako zakończoną
        if (fetchAllPages)
        {
            user.IsFirstSyncComplete = true;
            await db.SaveChangesAsync(ct);
        }

        _logger.LogInformation("[StravaSync] Zapisano {Count} nowych aktywności (metadane) dla userId={UserId}", newCount, request.UserId);
        return newCount;
    }

    /// <summary>
    /// Co 15 minut pobiera brakujące streamy i GPX (max 30 na cykl) dla wszystkich użytkowników.
    /// </summary>
    private async Task PeriodicStreamGpxSync(CancellationToken ct)
    {
        // Poczekaj 1 minutę po starcie żeby lista aktywności zdążyła się załadować
        await Task.Delay(TimeSpan.FromMinutes(1), ct);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await DownloadMissingStreamsAndGpx(ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[StravaSync] Błąd periodycznego pobierania streamów/GPX");
            }

            await Task.Delay(TimeSpan.FromMinutes(15), ct);
        }
    }

    private async Task DownloadMissingStreamsAndGpx(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var encryption = scope.ServiceProvider.GetRequiredService<EncryptionService>();

        // Pobierz użytkowników z tokenem
        var users = await db.Users
            .Where(u => u.IsFirstSyncComplete && u.EncryptedAccessToken != null)
            .ToListAsync(ct);

        foreach (var user in users)
        {
            if (ct.IsCancellationRequested) break;

            var accessToken = await GetValidAccessTokenAsync(db, user, encryption, ct);
            if (accessToken == null)
            {
                _logger.LogWarning("[StravaSync] Brak ważnego tokenu Strava dla userId={UserId} - pomijam", user.Id);
                continue;
            }

            // Pobierz do 30 aktywności bez streamu
            var activitiesWithoutStream = await db.Activities
                .Where(a => a.UserId == user.Id && a.Stream == null)
                .OrderByDescending(a => a.StartDate)
                .Take(30)
                .ToListAsync(ct);

            var client = _httpClientFactory.CreateClient();
            int downloaded = 0;

            foreach (var activity in activitiesWithoutStream)
            {
                if (ct.IsCancellationRequested) break;

                try
                {
                    await DownloadActivityDataAsync(client, accessToken, user, activity, encryption, ct);
                    await db.SaveChangesAsync(ct);
                    downloaded++;
                }
                catch (StravaRateLimitException)
                {
                    _logger.LogWarning("[StravaSync] Limit zapytań Strava (429) - przerywam cykl, wznowienie za 15 min");
                    return;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[StravaSync] Błąd pobierania stream/GPX dla aktywności {Id}", activity.StravaActivityId);
                }
            }

            // Pobierz GPX dla aktywności które mają stream ale nie mają GPX
            if (downloaded < 30)
            {
                var activitiesWithoutGpx = await db.Activities
                    .Include(a => a.Stream)
                    .Where(a => a.UserId == user.Id && a.Stream != null && a.EncryptedGpxData == null)
                    .OrderByDescending(a => a.StartDate)
                    .Take(30 - downloaded)
                    .ToListAsync(ct);

                foreach (var activity in activitiesWithoutGpx)
                {
                    try
                    {
                        var streamJson = encryption.Decrypt(activity.Stream!.EncryptedData, activity.Stream.IV, user.StravaAthleteId);
                        GenerateAndSaveGpx(activity, streamJson, encryption, user.StravaAthleteId);
                        await db.SaveChangesAsync(ct);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "[StravaSync] Błąd generowania GPX dla aktywności {Id}", activity.StravaActivityId);
                    }
                }
            }

            // Jednorazowe uzupełnienie prędkości z już pobranych JSON-ów (backfill po dodaniu kolumn AverageSpeed/MaxSpeed)
            var speedBackfill = await db.Activities
                .Where(a => a.UserId == user.Id && a.EncryptedActivityJson != null && a.MaxSpeed == null)
                .Take(200)
                .ToListAsync(ct);

            foreach (var activity in speedBackfill)
            {
                try
                {
                    var json = encryption.Decrypt(activity.EncryptedActivityJson!, activity.ActivityJsonIV!, user.StravaAthleteId);
                    FillSpeedsFromActivityJson(activity, json);
                }
                catch { /* uszkodzony wpis - oznaczamy niżej, żeby nie ponawiać */ }
                activity.MaxSpeed ??= 0;
            }
            if (speedBackfill.Count > 0)
            {
                await db.SaveChangesAsync(ct);
                _logger.LogInformation("[StravaSync] Backfill prędkości: uzupełniono {Count} aktywności dla userId={UserId}", speedBackfill.Count, user.Id);
            }

            _logger.LogInformation("[StravaSync] Periodic sync: pobrano {Count} streamów/GPX dla userId={UserId}", downloaded, user.Id);
        }
    }

    /// <summary>
    /// Pobiera stream i GPX on-demand dla konkretnej aktywności.
    /// Wywoływane z kontrolera gdy użytkownik wybiera aktywność.
    /// </summary>
    public async Task<bool> DownloadStreamAndGpxForActivity(int userId, long stravaActivityId)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var encryption = scope.ServiceProvider.GetRequiredService<EncryptionService>();

        var user = await db.Users.FindAsync(userId);
        if (user?.EncryptedAccessToken == null) return false;

        var accessToken = await GetValidAccessTokenAsync(db, user, encryption, CancellationToken.None);
        if (accessToken == null) return false;

        var activity = await db.Activities
            .Include(a => a.Stream)
            .FirstOrDefaultAsync(a => a.UserId == userId && a.StravaActivityId == stravaActivityId);

        if (activity == null) return false;

        var client = _httpClientFactory.CreateClient();
        var ok = await DownloadActivityDataAsync(client, accessToken, user, activity, encryption, CancellationToken.None);
        await db.SaveChangesAsync();
        return ok;
    }

    /// <summary>
    /// Pobiera ze Stravy brakujący pełny JSON aktywności oraz stream (szyfrując je na encji)
    /// i generuje GPX, gdy stream jest dostępny. Nie zapisuje zmian w bazie - to robi wołający.
    /// Zwraca false, gdy nie udało się pobrać streamu.
    /// </summary>
    private async Task<bool> DownloadActivityDataAsync(HttpClient client, string accessToken, AppUser user,
        ActivityRecord activity, EncryptionService encryption, CancellationToken ct)
    {
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        // Pełny JSON aktywności (jeśli brak)
        if (activity.EncryptedActivityJson == null)
        {
            var actResp = await client.GetAsync($"https://www.strava.com/api/v3/activities/{activity.StravaActivityId}", ct);
            if (actResp.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
                throw new StravaRateLimitException();
            if (actResp.IsSuccessStatusCode)
            {
                var actJson = await actResp.Content.ReadAsStringAsync(ct);
                var (encActJson, actJsonIv) = encryption.Encrypt(actJson, user.StravaAthleteId);
                activity.EncryptedActivityJson = encActJson;
                activity.ActivityJsonIV = actJsonIv;
                FillSpeedsFromActivityJson(activity, actJson);
            }
            else
            {
                _logger.LogWarning("[StravaSync] Nie udało się pobrać szczegółów aktywności {Id}: {Code}",
                    activity.StravaActivityId, actResp.StatusCode);
            }
        }

        // Stream (jeśli brak) + generowanie GPX
        if (activity.Stream == null)
        {
            var streamUrl = $"https://www.strava.com/api/v3/activities/{activity.StravaActivityId}/streams?keys=latlng,time,altitude,heartrate,distance,velocity_smooth&key_by_type=true";
            var streamResp = await client.GetAsync(streamUrl, ct);
            if (streamResp.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
                throw new StravaRateLimitException();
            if (!streamResp.IsSuccessStatusCode)
                return false;

            var streamJson = await streamResp.Content.ReadAsStringAsync(ct);
            var (encrypted, iv) = encryption.Encrypt(streamJson, user.StravaAthleteId);
            activity.Stream = new ActivityStream
            {
                EncryptedData = encrypted,
                IV = iv
            };
            GenerateAndSaveGpx(activity, streamJson, encryption, user.StravaAthleteId);
        }
        else if (activity.EncryptedGpxData == null)
        {
            // Ma stream, ale brak GPX - wygeneruj z zapisanego streamu
            var streamJson = encryption.Decrypt(activity.Stream.EncryptedData, activity.Stream.IV, user.StravaAthleteId);
            GenerateAndSaveGpx(activity, streamJson, encryption, user.StravaAthleteId);
        }

        return true;
    }

    /// <summary>
    /// Zwraca ważny access token użytkownika. Gdy token wygasł (lub wygasa w ciągu 5 minut),
    /// wymienia refresh token na nowy komplet i zapisuje go w bazie.
    /// Zwraca null, gdy odświeżenie nie jest możliwe - wtedy potrzebne jest ponowne logowanie przez Stravę.
    /// </summary>
    internal async Task<string?> GetValidAccessTokenAsync(AppDbContext db, AppUser user, EncryptionService encryption, CancellationToken ct)
    {
        if (user.EncryptedAccessToken == null)
            return null;

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (user.TokenExpiresAt.HasValue && user.TokenExpiresAt.Value > now + 300)
        {
            try { return encryption.DecryptToken(user.EncryptedAccessToken); }
            catch { return null; }
        }

        if (user.EncryptedRefreshToken == null)
        {
            // Brak refresh tokenu (stare konto) - spróbuj obecnym access tokenem, może jeszcze działa
            try { return encryption.DecryptToken(user.EncryptedAccessToken); }
            catch { return null; }
        }

        await _refreshLock.WaitAsync(ct);
        try
        {
            // Inna ścieżka mogła już odświeżyć token - sprawdź ponownie na świeżych danych z bazy
            await db.Entry(user).ReloadAsync(ct);
            now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            if (user.EncryptedAccessToken != null && user.TokenExpiresAt.HasValue && user.TokenExpiresAt.Value > now + 300)
            {
                try { return encryption.DecryptToken(user.EncryptedAccessToken); }
                catch { return null; }
            }

            string refreshToken;
            try { refreshToken = encryption.DecryptToken(user.EncryptedRefreshToken!); }
            catch { return null; }

            var client = _httpClientFactory.CreateClient();
            var content = new FormUrlEncodedContent(new[]
            {
                new KeyValuePair<string, string>("client_id", _configuration["Strava:ClientId"] ?? ""),
                new KeyValuePair<string, string>("client_secret", _configuration["Strava:ClientSecret"] ?? ""),
                new KeyValuePair<string, string>("grant_type", "refresh_token"),
                new KeyValuePair<string, string>("refresh_token", refreshToken)
            });

            var resp = await client.PostAsync("https://www.strava.com/oauth/token", content, ct);
            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogWarning("[StravaSync] Odświeżenie tokenu Strava nie powiodło się dla userId={UserId}: {Code}", user.Id, resp.StatusCode);
                return null;
            }

            var json = await resp.Content.ReadAsStringAsync(ct);
            var root = JsonDocument.Parse(json).RootElement;
            if (!root.TryGetProperty("access_token", out var at))
                return null;

            var newAccessToken = at.GetString()!;
            user.EncryptedAccessToken = encryption.EncryptToken(newAccessToken);
            if (root.TryGetProperty("refresh_token", out var rt) && rt.GetString() is { Length: > 0 } newRefresh)
                user.EncryptedRefreshToken = encryption.EncryptToken(newRefresh);
            if (root.TryGetProperty("expires_at", out var exp))
                user.TokenExpiresAt = exp.GetInt64();

            await db.SaveChangesAsync(ct);
            _logger.LogInformation("[StravaSync] Odświeżono token Strava dla userId={UserId}", user.Id);
            return newAccessToken;
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    /// <summary>
    /// Uzupełnia AverageSpeed/MaxSpeed [m/s] z JSON-a aktywności Stravy.
    /// </summary>
    private static void FillSpeedsFromActivityJson(ActivityRecord activity, string activityJson)
    {
        try
        {
            var root = JsonDocument.Parse(activityJson).RootElement;
            if (root.TryGetProperty("average_speed", out var avg)) activity.AverageSpeed = avg.GetDouble();
            if (root.TryGetProperty("max_speed", out var max)) activity.MaxSpeed = max.GetDouble();
        }
        catch { /* brak pól w JSON-ie nie jest błędem */ }
    }

    /// <summary>
    /// Strava odpowiedziała 429 - limit zapytań wyczerpany, cykl należy przerwać.
    /// </summary>
    private sealed class StravaRateLimitException : Exception { }

    private void GenerateAndSaveGpx(ActivityRecord activity, string streamJson, EncryptionService encryption, long stravaAthleteId)
    {
        var streams = JsonDocument.Parse(streamJson).RootElement;

        string? activityJsonStr = null;
        if (activity.EncryptedActivityJson != null && activity.ActivityJsonIV != null)
            activityJsonStr = encryption.Decrypt(activity.EncryptedActivityJson, activity.ActivityJsonIV, stravaAthleteId);

        var gpxContent = GenerateGpxFromStreams(activity, streams, activityJsonStr);
        var (encGpx, gpxIv) = encryption.Encrypt(gpxContent, stravaAthleteId);
        activity.EncryptedGpxData = encGpx;
        activity.GpxIV = gpxIv;
    }

    internal string GenerateGpxFromStreams(ActivityRecord activity, JsonElement streams, string? activityJson)
    {
        var name = activity.Name ?? "Trasa";
        var startDate = activity.StartDate?.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ");
        DateTime? startDateTime = activity.StartDate;

        if (activityJson != null)
        {
            try
            {
                var actDoc = JsonDocument.Parse(activityJson).RootElement;
                if (actDoc.TryGetProperty("start_date", out var sdEl))
                {
                    startDate = sdEl.GetString();
                    if (DateTime.TryParse(startDate, null, DateTimeStyles.AdjustToUniversal, out var dt))
                        startDateTime = dt;
                }
            }
            catch { }
        }

        var latlng = streams.TryGetProperty("latlng", out var latlngStream) ? latlngStream.GetProperty("data") : default;
        var time = streams.TryGetProperty("time", out var timeStream) ? timeStream.GetProperty("data") : default;
        var altitude = streams.TryGetProperty("altitude", out var altStream) ? altStream.GetProperty("data") : default;
        var heartrate = streams.TryGetProperty("heartrate", out var hrStream) ? hrStream.GetProperty("data") : default;

        var sb = new StringBuilder();
        sb.AppendLine("<?xml version=\"1.0\" encoding=\"UTF-8\"?>");
        sb.AppendLine("<gpx version=\"1.1\" creator=\"GpxApp\" xmlns=\"http://www.topografix.com/GPX/1/1\">");
        sb.AppendLine($"  <metadata><name>{System.Security.SecurityElement.Escape(name)}</name><time>{startDate}</time></metadata>");
        sb.AppendLine("  <trk><name>Activity</name><trkseg>");

        if (latlng.ValueKind == JsonValueKind.Array)
        {
            for (int i = 0; i < latlng.GetArrayLength(); i++)
            {
                var point = latlng[i];
                double lat = point[0].GetDouble();
                double lng = point[1].GetDouble();
                sb.Append($"    <trkpt lat=\"{lat.ToString(CultureInfo.InvariantCulture)}\" lon=\"{lng.ToString(CultureInfo.InvariantCulture)}\">");

                if (altitude.ValueKind == JsonValueKind.Array && i < altitude.GetArrayLength())
                    sb.Append($"<ele>{altitude[i].GetDouble().ToString(CultureInfo.InvariantCulture)}</ele>");

                if (startDateTime.HasValue && time.ValueKind == JsonValueKind.Array && i < time.GetArrayLength())
                {
                    int seconds = time[i].GetInt32();
                    var ptTime = startDateTime.Value.AddSeconds(seconds).ToUniversalTime();
                    sb.Append($"<time>{ptTime:yyyy-MM-ddTHH:mm:ssZ}</time>");
                }

                if (heartrate.ValueKind == JsonValueKind.Array && i < heartrate.GetArrayLength())
                    sb.Append($"<extensions><gpxtpx:TrackPointExtension xmlns:gpxtpx=\"http://www.garmin.com/xmlschemas/TrackPointExtension/v1\"><gpxtpx:hr>{heartrate[i].GetInt32()}</gpxtpx:hr></gpxtpx:TrackPointExtension></extensions>");

                sb.AppendLine("</trkpt>");
            }
        }
        sb.AppendLine("  </trkseg></trk></gpx>");
        return sb.ToString();
    }
}
