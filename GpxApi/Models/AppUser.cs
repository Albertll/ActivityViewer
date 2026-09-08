using System.ComponentModel.DataAnnotations;

namespace GpxApi.Models;

public class AppUser
{
    [Key]
    public int Id { get; set; }

    public long StravaAthleteId { get; set; }

    [MaxLength(200)]
    public string? StravaName { get; set; }

    [MaxLength(500)]
    public string? EncryptedAccessToken { get; set; }

    [MaxLength(500)]
    public string? EncryptedRefreshToken { get; set; }

    public long? TokenExpiresAt { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime? LastLoginAt { get; set; }

    /// <summary>
    /// Czy pierwsza pełna synchronizacja listy aktywności została zakończona
    /// </summary>
    public bool IsFirstSyncComplete { get; set; }

    public ICollection<ActivityRecord> Activities { get; set; } = new List<ActivityRecord>();
}
