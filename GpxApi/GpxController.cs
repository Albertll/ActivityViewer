using GpxApi.Data;
using GpxApi.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class GpxController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly EncryptionService _encryption;
    private readonly StravaActivitySyncService _syncService;

    public GpxController(AppDbContext db, EncryptionService encryption, StravaActivitySyncService syncService)
    {
        _db = db;
        _encryption = encryption;
        _syncService = syncService;
    }

    private int GetUserId() =>
        int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    private long GetStravaAthleteId() =>
        long.Parse(User.FindFirstValue("StravaAthleteId")!);

    /// <summary>
    /// Lista aktywności ze statystykami (zamiennik starego stats z plików)
    /// </summary>
    [HttpGet("stats")]
    public async Task<IActionResult> Stats()
    {
        var userId = GetUserId();
        var activities = await _db.Activities
            .Where(a => a.UserId == userId && a.EncryptedGpxData != null)
            .OrderByDescending(a => a.StartDate)
            .Select(a => new
            {
                name = a.StravaActivityId + ".gpx",
                activityId = a.StravaActivityId,
                activityName = a.Name,
                type = a.Type,
                start = a.StartDate,
                dist = a.Distance,
                duration = a.MovingTime,
                avgSpeed = a.MovingTime > 0 ? a.Distance / a.MovingTime : 0,
                maxSpeed = 0.0 // nie mamy tej wartości w metadanych
            })
            .ToListAsync();

        return Ok(activities);
    }

    /// <summary>
    /// Lista plików GPX (nazwy) - kompatybilność
    /// </summary>
    [HttpGet("list")]
    public async Task<IActionResult> List()
    {
        var userId = GetUserId();
        var files = await _db.Activities
            .Where(a => a.UserId == userId && a.EncryptedGpxData != null)
            .OrderByDescending(a => a.StartDate)
            .Select(a => a.StravaActivityId + ".gpx")
            .ToListAsync();

        return Ok(files);
    }

    /// <summary>
    /// Pobierz GPX po nazwie pliku (np. 12345.gpx) lub activityId
    /// </summary>
    [HttpGet("{filename}")]
    public async Task<IActionResult> Get(string filename)
    {
        var userId = GetUserId();
        var athleteId = GetStravaAthleteId();

        // Wyciągnij activityId z nazwy pliku
        var idStr = filename.Replace(".gpx", "");
        if (!long.TryParse(idStr, out var activityId))
            return BadRequest("Nieprawidłowy plik");

        var activity = await _db.Activities
            .FirstOrDefaultAsync(a => a.UserId == userId && a.StravaActivityId == activityId);

        if (activity?.EncryptedGpxData == null || activity.GpxIV == null)
            return NotFound();

        var gpxContent = _encryption.Decrypt(activity.EncryptedGpxData, activity.GpxIV, athleteId);
        return Content(gpxContent, "application/gpx+xml");
    }

    [HttpHead("{filename}")]
    public async Task<IActionResult> Head([FromRoute] string filename)
    {
        var userId = GetUserId();
        var idStr = filename.Replace(".gpx", "");
        if (!long.TryParse(idStr, out var activityId))
            return BadRequest("Nieprawidłowy plik");

        var exists = await _db.Activities
            .AnyAsync(a => a.UserId == userId && a.StravaActivityId == activityId && a.EncryptedGpxData != null);

        return exists ? Ok() : NotFound();
    }

    /// <summary>
    /// Generuj GPX - pobiera stream on-demand jeśli brak, generuje GPX i zapisuje w bazie
    /// </summary>
    [HttpPost("generate")]
    public async Task<IActionResult> GenerateGpx([FromBody] GenerateGpxRequest req)
    {
        if (string.IsNullOrEmpty(req.ActivityId))
            return BadRequest(new { error = "Brak activityId" });

        if (!long.TryParse(req.ActivityId, out var activityId))
            return BadRequest(new { error = "Nieprawidłowe activityId" });

        var userId = GetUserId();

        // Użyj on-demand download - pobierze stream i wygeneruje GPX
        var result = await _syncService.DownloadStreamAndGpxForActivity(userId, activityId);
        if (!result)
            return BadRequest(new { error = "Nie udało się pobrać danych ze Strava" });

        return Ok(new { file = $"{activityId}.gpx" });
    }

    public class GenerateGpxRequest
    {
        public string? ActivityId { get; set; }
    }
}
