// ============================================================
// Core/Services/StorageMonitorService.cs
// Business Rule: Monitor local disk + SQLite file size.
// Expose reactive properties so the sidebar storage bar updates live.
// ============================================================
using WorkSpaceApp.Infrastructure.Data;

namespace WorkSpaceApp.Core.Services;

public interface IStorageMonitorService
{
    /// <summary>Free bytes on the drive that hosts the SQLite database.</summary>
    long GetFreeDiskSpaceBytes();

    /// <summary>Current SQLite file size in bytes.</summary>
    long GetDatabaseSizeBytes();

    /// <summary>1 GB limit in bytes.</summary>
    long LimitBytes { get; }

    /// <summary>Usage ratio 0–1 (for the progress bar in the sidebar).</summary>
    double UsageRatio { get; }

    /// <summary>True when the DB has reached or exceeded the limit.</summary>
    bool IsLimitReached { get; }
}

public sealed class StorageMonitorService : IStorageMonitorService
{
    private readonly AppDbContext _db;

    // Business Rule: hard cap at 1 GB
    public long LimitBytes => 1L * 1024 * 1024 * 1024;

    public StorageMonitorService(AppDbContext db) => _db = db;

    public long GetFreeDiskSpaceBytes()
    {
        string dbPath = AppDbContext.GetDatabaseFilePath();
        string? drive = Path.GetPathRoot(dbPath);
        if (drive is null) return long.MaxValue;

        var info = new DriveInfo(drive);
        return info.AvailableFreeSpace;
    }

    public long GetDatabaseSizeBytes() => _db.GetDatabaseFileSizeBytes();

    public double UsageRatio =>
        Math.Clamp((double)GetDatabaseSizeBytes() / LimitBytes, 0, 1);

    public bool IsLimitReached => GetDatabaseSizeBytes() >= LimitBytes;
}
