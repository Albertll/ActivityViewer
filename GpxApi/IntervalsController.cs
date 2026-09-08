using GpxApi.Data;
using GpxApi.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

[ApiController]
[Route("api/intervals")]
[Authorize]
public class IntervalsController : ApiControllerBase
{
    private readonly AppDbContext _db;

    public IntervalsController(AppDbContext db)
    {
        _db = db;
    }

    [HttpGet("{activityId}")]
    public async Task<IActionResult> Get(long activityId)
    {
        var userId = GetUserId();
        var activity = await _db.Activities
            .Include(a => a.Intervals)
            .FirstOrDefaultAsync(a => a.UserId == userId && a.StravaActivityId == activityId);

        if (activity == null)
            return NotFound(new { error = "Nie znaleziono aktywności" });

        var interval = activity.Intervals.FirstOrDefault();
        if (interval == null)
            return NotFound(new { error = "Brak zapisanych interwałów" });

        return Content(interval.IntervalsJson, "application/json");
    }

    [HttpPost("{activityId}")]
    public async Task<IActionResult> Save(long activityId)
    {
        var userId = GetUserId();

        using var reader = new StreamReader(Request.Body);
        var json = await reader.ReadToEndAsync();

        try { JsonDocument.Parse(json); }
        catch { return BadRequest(new { error = "Nieprawidłowy JSON" }); }

        var activity = await _db.Activities
            .Include(a => a.Intervals)
            .FirstOrDefaultAsync(a => a.UserId == userId && a.StravaActivityId == activityId);

        if (activity == null)
            return NotFound(new { error = "Nie znaleziono aktywności" });

        var interval = activity.Intervals.FirstOrDefault();
        if (interval != null)
        {
            interval.IntervalsJson = json;
        }
        else
        {
            activity.Intervals.Add(new IntervalRecord { IntervalsJson = json });
        }

        await _db.SaveChangesAsync();
        return Ok(new { message = "Interwały zapisane", activityId });
    }
}
