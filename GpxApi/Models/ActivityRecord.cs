using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace GpxApi.Models;

public class ActivityRecord
{
    [Key]
    public int Id { get; set; }

    public int UserId { get; set; }

    [ForeignKey(nameof(UserId))]
    public AppUser User { get; set; } = null!;

    public long StravaActivityId { get; set; }

    [MaxLength(300)]
    public string? Name { get; set; }

    [MaxLength(50)]
    public string? Type { get; set; }

    public double Distance { get; set; }

    public int MovingTime { get; set; }

    public DateTime? StartDate { get; set; }

    public double TotalElevationGain { get; set; }

    /// <summary>
    /// Średnia prędkość [m/s] z metadanych Stravy (null = jeszcze nie uzupełnione)
    /// </summary>
    public double? AverageSpeed { get; set; }

    /// <summary>
    /// Maksymalna prędkość [m/s] z metadanych Stravy (null = jeszcze nie uzupełnione)
    /// </summary>
    public double? MaxSpeed { get; set; }

    /// <summary>
    /// Zaszyfrowany pełny JSON aktywności ze Stravy (AES-256-CBC)
    /// </summary>
    public byte[]? EncryptedActivityJson { get; set; }

    /// <summary>
    /// IV dla szyfrowania ActivityJson
    /// </summary>
    public byte[]? ActivityJsonIV { get; set; }

    /// <summary>
    /// Zaszyfrowane dane GPX (AES-256-CBC)
    /// </summary>
    public byte[]? EncryptedGpxData { get; set; }

    /// <summary>
    /// IV dla szyfrowania GPX
    /// </summary>
    public byte[]? GpxIV { get; set; }

    public ActivityStream? Stream { get; set; }

    public ICollection<IntervalRecord> Intervals { get; set; } = new List<IntervalRecord>();
}
