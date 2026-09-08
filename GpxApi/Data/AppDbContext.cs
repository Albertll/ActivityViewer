using GpxApi.Models;
using Microsoft.EntityFrameworkCore;

namespace GpxApi.Data;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<AppUser> Users => Set<AppUser>();
    public DbSet<ActivityRecord> Activities => Set<ActivityRecord>();
    public DbSet<ActivityStream> ActivityStreams => Set<ActivityStream>();
    public DbSet<IntervalRecord> Intervals => Set<IntervalRecord>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<AppUser>(e =>
        {
            e.HasIndex(u => u.StravaAthleteId).IsUnique();
        });

        modelBuilder.Entity<ActivityRecord>(e =>
        {
            e.HasIndex(a => new { a.UserId, a.StravaActivityId }).IsUnique();
            e.HasOne(a => a.User)
             .WithMany(u => u.Activities)
             .HasForeignKey(a => a.UserId)
             .OnDelete(DeleteBehavior.Cascade);
            // StartDate trzymamy w UTC; kolumna datetime2 nie niesie strefy, więc oznaczamy Kind przy odczycie
            e.Property(a => a.StartDate)
             .HasConversion(
                 v => v,
                 v => v == null ? v : DateTime.SpecifyKind(v.Value, DateTimeKind.Utc));
        });

        modelBuilder.Entity<ActivityStream>(e =>
        {
            e.HasOne(s => s.Activity)
             .WithOne(a => a.Stream)
             .HasForeignKey<ActivityStream>(s => s.ActivityRecordId)
             .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<IntervalRecord>(e =>
        {
            e.HasOne(i => i.Activity)
             .WithMany(a => a.Intervals)
             .HasForeignKey(i => i.ActivityRecordId)
             .OnDelete(DeleteBehavior.Cascade);
        });
    }
}
