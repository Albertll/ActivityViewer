using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;

/// <summary>
/// Wspólna baza kontrolerów API - odczyt tożsamości zalogowanego użytkownika z claimów cookie.
/// </summary>
public abstract class ApiControllerBase : ControllerBase
{
    protected int GetUserId() =>
        int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    protected long GetStravaAthleteId() =>
        long.Parse(User.FindFirstValue("StravaAthleteId")!);
}
