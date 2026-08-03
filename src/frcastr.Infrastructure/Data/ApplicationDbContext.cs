using frcastr.Core.Entities;
using frcastr.Core.Enums;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace frcastr.Infrastructure.Data;

public class ApplicationDbContext : IdentityDbContext<ApplicationUser>
{
    public ApplicationDbContext(DbContextOptions<ApplicationDbContext> options) : base(options) { }

    public DbSet<Setting>                 Settings                 => Set<Setting>();
    public DbSet<Permission>              Permissions              => Set<Permission>();
    public DbSet<AuditLog>                AuditLogs                => Set<AuditLog>();
    public DbSet<WeatherReading>          WeatherReadings          => Set<WeatherReading>();
    public DbSet<WeatherReadingAggregate> WeatherReadingAggregates => Set<WeatherReadingAggregate>();
    public DbSet<WeatherChannelRecord>    WeatherChannelRecords    => Set<WeatherChannelRecord>();
    public DbSet<DataSource>              DataSources              => Set<DataSource>();
    public DbSet<Device>                  Devices                  => Set<Device>();
    public DbSet<ForecastCache>           ForecastCaches           => Set<ForecastCache>();
    public DbSet<AlertCache>              AlertCaches              => Set<AlertCache>();
    public DbSet<WebhookAlert>            WebhookAlerts            => Set<WebhookAlert>();
    public DbSet<WidgetDefinition>        WidgetDefinitions        => Set<WidgetDefinition>();
    public DbSet<DashboardLayout>         DashboardLayouts         => Set<DashboardLayout>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.Entity<Setting>(e =>
        {
            e.HasIndex(s => s.Key).IsUnique();
            e.Property(s => s.Key).IsRequired().HasMaxLength(250);
            e.Property(s => s.Value).IsRequired();
            e.Property(s => s.Description).HasMaxLength(500);
            e.Property(s => s.LastModifiedBy).HasMaxLength(450);
        });

        builder.Entity<Permission>(e =>
        {
            e.Property(p => p.Name).HasMaxLength(200);
            e.Property(p => p.Description).HasMaxLength(500);
            e.Property(p => p.RoleId).HasMaxLength(450);
            e.Property(p => p.Resource).HasMaxLength(100);
            e.Property(p => p.Action).HasMaxLength(50);
            e.HasIndex(p => new { p.RoleId, p.Resource, p.Action })
             .HasFilter("[RoleId] IS NOT NULL AND [Resource] IS NOT NULL AND [Action] IS NOT NULL")
             .IsUnique();
        });

        builder.Entity<AuditLog>(e =>
        {
            e.Property(a => a.EventType).IsRequired().HasMaxLength(100);
            e.Property(a => a.UserId).HasMaxLength(450);
            e.Property(a => a.UserName).HasMaxLength(256);
            e.Property(a => a.IpAddress).HasMaxLength(45);
            e.Property(a => a.EntityType).HasMaxLength(100);
            e.Property(a => a.EntityId).HasMaxLength(450);
            e.Property(a => a.EntityName).HasMaxLength(500);
            e.HasIndex(a => a.CreatedAt);
            e.HasIndex(a => a.RetainUntil).HasFilter("[RetainUntil] IS NOT NULL");
            e.HasQueryFilter(a => a.RetainUntil == null || a.RetainUntil > DateTime.UtcNow);
        });

        builder.Entity<WeatherReading>(e =>
        {
            e.Property(r => r.ChannelName).IsRequired().HasMaxLength(100);
            e.Property(r => r.Unit).IsRequired().HasMaxLength(20);
            e.Property(r => r.Value).HasPrecision(10, 4);
            e.HasIndex(r => r.Timestamp);
            e.HasIndex(r => new { r.ChannelName, r.Timestamp });
            e.HasIndex(r => r.SourceId);
            e.HasIndex(r => new { r.DeviceId, r.ChannelName, r.Timestamp });
            e.HasOne(r => r.Source).WithMany()
             .HasForeignKey(r => r.SourceId)
             .OnDelete(DeleteBehavior.Restrict);
            e.HasOne(r => r.Device).WithMany()
             .HasForeignKey(r => r.DeviceId)
             .OnDelete(DeleteBehavior.SetNull);
        });

        builder.Entity<WeatherReadingAggregate>(e =>
        {
            e.Property(a => a.ChannelName).IsRequired().HasMaxLength(100);
            e.Property(a => a.Unit).IsRequired().HasMaxLength(20);
            e.Property(a => a.Avg).HasPrecision(10, 4);
            e.Property(a => a.Min).HasPrecision(10, 4);
            e.Property(a => a.Max).HasPrecision(10, 4);
            e.Property(a => a.Granularity).HasConversion<string>().HasMaxLength(10);
            e.HasIndex(a => new { a.ChannelName, a.Granularity, a.PeriodStart });
            e.HasIndex(a => a.SourceId);
            e.HasIndex(a => new { a.DeviceId, a.ChannelName, a.Granularity, a.PeriodStart });
            // One row per bucket, matching the key the aggregation services dedup on. That dedup
            // silently skipping a row used to mean the raw readings behind it were deleted without
            // ever being rolled up; this turns any recurrence into a failed transaction instead of
            // missing history. Source is part of the key because two pull sources can legitimately
            // report the same channel for the same hour. HasFilter(null) for the same reason as
            // WeatherChannelRecord below: a station-wide row has a null DeviceId and must still be
            // unique, which SQL Server's NULLs-compare-equal semantics give us only unfiltered.
            e.HasIndex(a => new { a.ChannelName, a.SourceId, a.DeviceId, a.Granularity, a.PeriodStart })
             .IsUnique().HasFilter(null);
            e.HasOne(a => a.Source).WithMany()
             .HasForeignKey(a => a.SourceId)
             .OnDelete(DeleteBehavior.Restrict);
            e.HasOne(a => a.Device).WithMany()
             .HasForeignKey(a => a.DeviceId)
             .OnDelete(DeleteBehavior.SetNull);
        });

        builder.Entity<WeatherChannelRecord>(e =>
        {
            e.Property(r => r.ChannelName).IsRequired().HasMaxLength(100);
            e.Property(r => r.AllTimeMax).HasPrecision(10, 4);
            e.Property(r => r.AllTimeMin).HasPrecision(10, 4);
            // Records are per (channel, device). HasFilter(null) overrides EF's default
            // "[DeviceId] IS NOT NULL" filter for nullable unique indexes: SQL Server treats NULLs
            // as equal, so the unfiltered index still admits exactly one station-wide row per
            // channel. With the default filter those rows would lose uniqueness entirely.
            e.HasIndex(r => new { r.ChannelName, r.DeviceId }).IsUnique().HasFilter(null);
            e.HasOne(r => r.Device).WithMany()
             .HasForeignKey(r => r.DeviceId)
             .OnDelete(DeleteBehavior.SetNull);
        });

        builder.Entity<DataSource>(e =>
        {
            e.Property(s => s.Name).IsRequired().HasMaxLength(200);
            e.Property(s => s.Type).HasConversion<string>().HasMaxLength(20);
            e.Property(s => s.Url).HasMaxLength(2000);
            e.HasIndex(s => s.Name).IsUnique();
        });

        builder.Entity<Device>(e =>
        {
            e.Property(d => d.DeviceId).IsRequired().HasMaxLength(100);
            e.Property(d => d.Name).IsRequired().HasMaxLength(200);
            e.Property(d => d.Location).HasMaxLength(200);
            e.Property(d => d.Model).HasMaxLength(100);
            e.Property(d => d.FirmwareVersion).HasMaxLength(50);
            e.Property(d => d.ChannelOverrides).HasMaxLength(2000);
            e.HasIndex(d => d.DeviceId).IsUnique();
            e.HasIndex(d => d.LastSeenAt);
            e.HasOne(d => d.Source).WithMany()
             .HasForeignKey(d => d.SourceId)
             .OnDelete(DeleteBehavior.SetNull);
        });

        builder.Entity<ForecastCache>(e =>
        {
            e.HasIndex(f => new { f.SourceId, f.IsHourly, f.FetchedAt });
            e.HasOne(f => f.Source).WithMany()
             .HasForeignKey(f => f.SourceId)
             .OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<AlertCache>(e =>
        {
            e.HasIndex(a => new { a.SourceId, a.FetchedAt });
            e.HasOne(a => a.Source).WithMany()
             .HasForeignKey(a => a.SourceId)
             .OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<WebhookAlert>(e =>
        {
            e.Property(w => w.Name).IsRequired().HasMaxLength(200);
            e.Property(w => w.Channel).IsRequired().HasMaxLength(100);
            e.Property(w => w.Unit).IsRequired().HasMaxLength(20);
            e.Property(w => w.WebhookUrl).IsRequired().HasMaxLength(2000);
            e.Property(w => w.Threshold).HasPrecision(10, 4);
            e.Property(w => w.Operator).HasConversion<string>().HasMaxLength(10);
            e.HasIndex(w => w.Channel);
        });

        builder.Entity<WidgetDefinition>(e =>
        {
            e.Property(w => w.Title).IsRequired().HasMaxLength(200);
            e.Property(w => w.Type).HasConversion<int>();
            e.Property(w => w.OwnerId).HasMaxLength(450);
            e.Property(w => w.DashboardName).IsRequired().HasMaxLength(200);
            e.HasIndex(w => w.SortOrder);
            e.HasIndex(w => w.DashboardName);
        });

        builder.Entity<DashboardLayout>(e =>
        {
            e.Property(l => l.OwnerId).HasMaxLength(450);
            e.Property(l => l.Name).IsRequired().HasMaxLength(200);
            e.HasIndex(l => new { l.OwnerId, l.Name }).IsUnique()
             .HasFilter("[OwnerId] IS NOT NULL");
        });
    }
}
