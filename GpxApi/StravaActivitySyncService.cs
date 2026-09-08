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
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

public class StravaActivitySyncService : BackgroundService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<StravaActivitySyncService> _logger;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ConcurrentQueue<SyncRequest> _syncQueue = new();
    private readonly SemaphoreSlim _signal = new(0);

    public record SyncRequest(int UserId, string AccessToken, long StravaAthleteId, bool IsFirstLogin);

    public StravaActivitySyncService(
        IHttpClientFactory httpClientFactory,
        ILogger<StravaActivitySyncService> logger,
        IServiceScopeFactory scopeFactory)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
        _scopeFactory = scopeFactory;
    }

    public void TriggerSync(int userId, string accessToken, long stravaAthleteId, bool isFirstLogin)
    {
        _syncQueue.Enqueue(new SyncRequest(userId, accessToken, stravaAthleteId, isFirstLogin));
        _signal.Release();
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
                    await SyncActivityList(request, stoppingToken);
                    _logger.LogInformation("[StravaSync] Sync finished for userId={UserId}", request.UserId);
                }
                catch (Exception ex)
                {
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
    /// </summary>
    private async Task SyncActivityList(SyncRequest request, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

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
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", request.AccessToken);

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
                    StartDate = act.TryGetProperty("start_date", out var sd) ? DateTime.Parse(sd.GetString()!) : null,
                    TotalElevationGain = act.TryGetProperty("total_elevation_gain", out var eg) ? eg.GetDouble() : 0
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
            var user = await db.Users.FindAsync(request.UserId);
            if (user != null)
            {
                user.IsFirstSyncComplete = true;
                await db.SaveChangesAsync(ct);
            }
        }

        _logger.LogInformation("[StravaSync] Zapisano {Count} nowych aktywności (metadane) dla userId={UserId}", newCount, request.UserId);
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

            string accessToken;
            try
            {
                accessToken = encryption.DecryptToken(user.EncryptedAccessToken!);
            }
            catch
            {
                _logger.LogWarning("[StravaSync] Nie można odszyfrować tokenu dla userId={UserId}", user.Id);
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
                    // Pobierz szczegóły aktywności (pełny JSON)
                    if (activity.EncryptedActivityJson == null)
                    {
                        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
                        var actResp = await client.GetAsync($"https://www.strava.com/api/v3/activities/{activity.StravaActivityId}", ct);
                        if (actResp.IsSuccessStatusCode)
                        {
                            var actJson = await actResp.Content.ReadAsStringAsync(ct);
                            var (encActJson, actJsonIv) = encryption.Encrypt(actJson, user.StravaAthleteId);
                            activity.EncryptedActivityJson = encActJson;
                            activity.ActivityJsonIV = actJsonIv;
                        }
                        else
                        {
                            _logger.LogWarning("[StravaSync] Nie udało się pobrać szczegółów aktywności {Id}: {Code}",
                                activity.StravaActivityId, actResp.StatusCode);
                            continue;
                        }
                    }

                    // Pobierz stream
                    var streamUrl = $"https://www.strava.com/api/v3/activities/{activity.StravaActivityId}/streams?keys=latlng,time,altitude,heartrate,distance,velocity_smooth&key_by_type=true";
                    client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
                    var streamResp = await client.GetAsync(streamUrl, ct);

                    if (streamResp.IsSuccessStatusCode)
                    {
                        var streamJson = await streamResp.Content.ReadAsStringAsync(ct);
                        var (encrypted, iv) = encryption.Encrypt(streamJson, user.StravaAthleteId);
                        activity.Stream = new ActivityStream
                        {
                            EncryptedData = encrypted,
                            IV = iv
                        };

                        // Generuj GPX od razu
                        GenerateAndSaveGpx(activity, streamJson, encryption, user.StravaAthleteId);
                    }

                    await db.SaveChangesAsync(ct);
                    downloaded++;
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

        string accessToken;
        try { accessToken = encryption.DecryptToken(user.EncryptedAccessToken); }
        catch { return false; }

        var activity = await db.Activities
            .Include(a => a.Stream)
            .FirstOrDefaultAsync(a => a.UserId == userId && a.StravaActivityId == stravaActivityId);

        if (activity == null) return false;

        var client = _httpClientFactory.CreateClient();

        // Pobierz szczegóły aktywności jeśli brak
        if (activity.EncryptedActivityJson == null)
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            var actResp = await client.GetAsync($"https://www.strava.com/api/v3/activities/{stravaActivityId}");
            if (actResp.IsSuccessStatusCode)
            {
                var actJson = await actResp.Content.ReadAsStringAsync();
                var (enc, iv) = encryption.Encrypt(actJson, user.StravaAthleteId);
                activity.EncryptedActivityJson = enc;
                activity.ActivityJsonIV = iv;
            }
        }

        // Pobierz stream jeśli brak
        if (activity.Stream == null)
        {
            var streamUrl = $"https://www.strava.com/api/v3/activities/{stravaActivityId}/streams?keys=latlng,time,altitude,heartrate,distance,velocity_smooth&key_by_type=true";
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            var streamResp = await client.GetAsync(streamUrl);
            if (!streamResp.IsSuccessStatusCode) return false;

            var streamJson = await streamResp.Content.ReadAsStringAsync();
            var (encrypted, iv) = encryption.Encrypt(streamJson, user.StravaAthleteId);
            activity.Stream = new ActivityStream
            {
                EncryptedData = encrypted,
                IV = iv
            };

            // Generuj GPX
            GenerateAndSaveGpx(activity, streamJson, encryption, user.StravaAthleteId);
        }
        else if (activity.EncryptedGpxData == null)
        {
            // Ma stream ale brak GPX - wygeneruj
            var streamJson = encryption.Decrypt(activity.Stream.EncryptedData, activity.Stream.IV, user.StravaAthleteId);
            GenerateAndSaveGpx(activity, streamJson, encryption, user.StravaAthleteId);
        }

        await db.SaveChangesAsync();
        return true;
    }

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

    private string GenerateGpxFromStreams(ActivityRecord activity, JsonElement streams, string? activityJson)
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
