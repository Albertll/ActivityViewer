using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace GpxApi.Models;

public class ActivityStream
{
    [Key]
    public int Id { get; set; }

    public int ActivityRecordId { get; set; }

    [ForeignKey(nameof(ActivityRecordId))]
    public ActivityRecord Activity { get; set; } = null!;

    /// <summary>
    /// Zaszyfrowane dane streamu (AES-256-CBC)
    /// </summary>
    public byte[] EncryptedData { get; set; } = Array.Empty<byte>();

    /// <summary>
    /// Wektor inicjalizacyjny AES
    /// </summary>
    public byte[] IV { get; set; } = Array.Empty<byte>();
}
