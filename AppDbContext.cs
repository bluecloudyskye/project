// ============================================================
// Infrastructure/Data/AppDbContext.cs
// Business Rule: SQLite local database with 1 GB soft limit.
// The context exposes a helper to check current DB file size.
// 
// Data Flow:
//   App Startup → EnsureCreatedAsync() → Creates SQLite file if missing
//   Before Sync/Download → EnforceStorageLimit() → Throws if >= 1GB
//   EF Core Operations → Notes/DynamicTables/ChangeLogs tables
// ============================================================
using Microsoft.EntityFrameworkCore;
using WorkSpaceApp.Core.Models;

namespace WorkSpaceApp.Infrastructure.Data;

/// <summary>
/// EF Core DbContext targeting SQLite.
/// Connection string is resolved from a fixed local path:
///   %LOCALAPPDATA%\WorkSpaceApp\workspace.db
/// </summary>
public class AppDbContext : DbContext
{
    // ── Tables ────────────────────────────────────────────────
    public DbSet<Note>                 Notes                 => Set<Note>();
    public DbSet<Tag>                  Tags                  => Set<Tag>();
    public DbSet<NoteTag>              NoteTags              => Set<NoteTag>();
    public DbSet<FileTag>              FileTags              => Set<FileTag>();
    public DbSet<DynamicTable>         DynamicTables         => Set<DynamicTable>();
    public DbSet<DynamicRow>           DynamicRows           => Set<DynamicRow>();
    public DbSet<ChangeLog>            ChangeLogs            => Set<ChangeLog>();
    public DbSet<UserRole>             UserRoles             => Set<UserRole>();
    public DbSet<HardwareRegistration> HardwareRegistrations => Set<HardwareRegistration>();

    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    protected override void OnModelCreating(ModelBuilder mb)
    {
        // ── NoteTag composite PK ──────────────────────────────
        mb.Entity<NoteTag>()
          .HasKey(nt => new { nt.NoteId, nt.TagId });

        mb.Entity<NoteTag>()
          .HasOne(nt => nt.Note)
          .WithMany(n => n.NoteTags)
          .HasForeignKey(nt => nt.NoteId);

        mb.Entity<NoteTag>()
          .HasOne(nt => nt.Tag)
          .WithMany(t => t.NoteTags)
          .HasForeignKey(nt => nt.TagId);

        // ── FileTag ───────────────────────────────────────────
        mb.Entity<FileTag>()
          .HasIndex(ft => ft.FilePath);

        mb.Entity<FileTag>()
          .HasOne(ft => ft.Tag)
          .WithMany(t => t.FileTags)
          .HasForeignKey(ft => ft.TagId)
          .OnDelete(DeleteBehavior.Cascade);

        // ── ChangeLog – never cascade delete audit records ────
        mb.Entity<ChangeLog>()
          .HasOne(cl => cl.Note)
          .WithMany(n => n.ChangeLogs)
          .HasForeignKey(cl => cl.NoteId)
          .OnDelete(DeleteBehavior.SetNull);

        mb.Entity<ChangeLog>()
          .HasOne(cl => cl.DynamicTable)
          .WithMany(dt => dt.ChangeLogs)
          .HasForeignKey(cl => cl.DynamicTableId)
          .OnDelete(DeleteBehavior.SetNull);

        // ── UserRole ──────────────────────────────────────────
        mb.Entity<UserRole>()
          .HasOne(ur => ur.Note)
          .WithMany(n => n.UserRoles)
          .HasForeignKey(ur => ur.NoteId)
          .OnDelete(DeleteBehavior.Cascade);

        // ── Indexes for common query paths ───────────────────
        mb.Entity<Note>()
          .HasIndex(n => n.OwnerHardwareId);

        mb.Entity<ChangeLog>()
          .HasIndex(cl => cl.ChangedAt);

        mb.Entity<HardwareRegistration>()
          .HasIndex(hr => hr.HardwareId)
          .IsUnique();
    }

    // ─────────────────────────────────────────────────────────
    // Business Rule: Storage limit check (1 GB hard cap).
    // Call before any sync/download operation.
    // ─────────────────────────────────────────────────────────
    private const long StorageLimitBytes = 1L * 1024 * 1024 * 1024; // 1 GB

    /// <summary>
    /// Returns the current SQLite file size in bytes.
    /// Throws <see cref="StorageLimitExceededException"/> if >= 1 GB.
    /// </summary>
    public long GetDatabaseFileSizeBytes()
    {
        var dbPath = GetDatabaseFilePath();
        return File.Exists(dbPath) ? new FileInfo(dbPath).Length : 0L;
    }

    /// <summary>
    /// Business Rule: Must be called before any sync/download operation.
    /// Throws if the local DB has reached the 1 GB limit.
    /// </summary>
    public void EnforceStorageLimit()
    {
        long size = GetDatabaseFileSizeBytes();
        if (size >= StorageLimitBytes)
            throw new StorageLimitExceededException(size, StorageLimitBytes);
    }

    public static string GetDatabaseFilePath()
    {
        string dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WorkSpaceApp");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, "workspace.db");
    }
}

/// <summary>Thrown when the local SQLite file exceeds the 1 GB limit.</summary>
public sealed class StorageLimitExceededException(long current, long limit)
    : Exception($"Storage limit exceeded: {current / 1_048_576} MB used of {limit / 1_048_576} MB allowed.")
{
    public long CurrentBytes { get; } = current;
    public long LimitBytes   { get; } = limit;
}
