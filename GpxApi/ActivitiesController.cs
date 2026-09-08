using GpxApi.Data;
using GpxApi.Models;
using GpxApi.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

[ApiController]
[Route("api/activities")]
[Authorize]
public class ActivitiesController : ApiControllerBase
{
    private readonly AppDbContext _db;
    private readonly EncryptionService _encryption;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly StravaActivitySyncService _syncService;

    public ActivitiesController(AppDbContext db, EncryptionService encryption, IHttpClientFactory httpClientFactory, StravaActivitySyncService syncService)
    {
        _db = db;
        _encryption = encryption;
        _httpClientFactory = httpClientFactory;
        _syncService = syncService;
    }

    [HttpGet("list")]
    public async Task<IActionResult> List()
    {
        var userId = GetUserId();
        var activities = await _db.Activities
            .Where(a => a.UserId == userId)
            .OrderByDescending(a => a.StartDate)
            .Select(a => new
            {
                id = a.StravaActivityId,
                name = a.Name,
                type = a.Type,
                distance = a.Distance,
                moving_time = a.MovingTime,
                start_date = a.StartDate,
                total_elevation_gain = a.TotalElevationGain,
                has_stream = a.Stream != null,
                has_gpx = a.EncryptedGpxData != null
            })
            .ToListAsync();

        return Ok(activities);
    }

    [HttpGet("{activityId}")]
    public async Task<IActionResult> GetActivity(long activityId)
    {
        var userId = GetUserId();
        var athleteId = GetStravaAthleteId();
        var activity = await _db.Activities
            .FirstOrDefaultAsync(a => a.UserId == userId && a.StravaActivityId == activityId);

        if (activity == null)
            return NotFound(new { error = "Nie znaleziono aktywności" });

        if (activity.EncryptedActivityJson != null && activity.ActivityJsonIV != null)
        {
            try
            {
                var json = _encryption.Decrypt(activity.EncryptedActivityJson, activity.ActivityJsonIV, athleteId);
                return Content(json, "application/json");
            }
            catch (Exception)
            {
                return StatusCode(500, new { error = "Nie można odszyfrować danych aktywności - sprawdź Encryption:MasterKey" });
            }
        }

        return Ok(new
        {
            id = activity.StravaActivityId,
            name = activity.Name,
            type = activity.Type,
            distance = activity.Distance,
            moving_time = activity.MovingTime,
            start_date = activity.StartDate,
            total_elevation_gain = activity.TotalElevationGain
        });
    }

    [HttpGet("{activityId}/has-stream")]
    public async Task<IActionResult> HasStream(long activityId)
    {
        var userId = GetUserId();
        var exists = await _db.Activities
            .Where(a => a.UserId == userId && a.StravaActivityId == activityId)
            .AnyAsync(a => a.Stream != null);

        return Ok(new { exists });
    }

    [HttpGet("{activityId}/stream")]
    public async Task<IActionResult> GetStream(long activityId)
    {
        var userId = GetUserId();
        var athleteId = GetStravaAthleteId();

        var activity = await _db.Activities
            .Include(a => a.Stream)
            .FirstOrDefaultAsync(a => a.UserId == userId && a.StravaActivityId == activityId);

        if (activity?.Stream == null)
            return NotFound(new { error = "Brak danych stream" });

        try
        {
            var decryptedJson = _encryption.Decrypt(
                activity.Stream.EncryptedData,
                activity.Stream.IV,
                athleteId);

            return Content(decryptedJson, "application/json");
        }
        catch (Exception)
        {
            return StatusCode(500, new { error = "Nie można odszyfrować streamu - sprawdź Encryption:MasterKey" });
        }
    }

    /// <summary>
    /// Ręcznie dociąga nowe aktywności ze Stravy (sync w tle; token odświeżany automatycznie)
    /// </summary>
    [HttpPost("sync")]
    public async Task<IActionResult> Sync()
    {
        var userId = GetUserId();
        var user = await _db.Users.FindAsync(userId);
        if (user?.EncryptedAccessToken == null)
            return BadRequest(new { error = "Brak tokenu Strava - zaloguj się ponownie przez Stravę" });

        var syncTask = _syncService.TriggerSyncAndWaitAsync(userId, user.StravaAthleteId, !user.IsFirstSyncComplete);
        var finished = await Task.WhenAny(syncTask, Task.Delay(TimeSpan.FromSeconds(60)));
        if (finished != syncTask)
            return Ok(new { queued = true }); // pierwsza pełna synchronizacja może trwać dłużej - kończy się w tle

        try
        {
            return Ok(new { added = await syncTask });
        }
        catch (Exception)
        {
            return StatusCode(502, new { error = "Synchronizacja ze Stravą nie powiodła się" });
        }
    }

    /// <summary>
    /// Pobiera stream i GPX on-demand dla aktywności (używa serwisu sync)
    /// </summary>
    [HttpPost("{activityId}/download-stream")]
    public async Task<IActionResult> DownloadStream(long activityId)
    {
        var userId = GetUserId();
        var result = await _syncService.DownloadStreamAndGpxForActivity(userId, activityId);

        if (!result)
            return BadRequest(new { error = "Nie udało się pobrać danych. Sprawdź token Strava." });

        return Ok(new { success = true });
    }
}
