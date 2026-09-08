using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace GpxApi.Models;

public class IntervalRecord
{
    [Key]
    public int Id { get; set; }

    public int ActivityRecordId { get; set; }

    [ForeignKey(nameof(ActivityRecordId))]
    public ActivityRecord Activity { get; set; } = null!;

    /// <summary>
    /// JSON z definicją interwałów
    /// </summary>
    public string IntervalsJson { get; set; } = "[]";
}
