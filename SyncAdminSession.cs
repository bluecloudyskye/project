// ============================================================
// SyncAdminSession.cs
// Runs on the admin (folder owner) side of a sync share.
// Listens for incoming requests and serves files automatically.
// Created when admin clicks "Share Folder"; disposed on disconnect.
// ============================================================
using Microsoft.EntityFrameworkCore;
using WorkSpaceApp.Core.Models;
using WorkSpaceApp.Core.Services;
using WorkSpaceApp.Infrastructure.Data;

namespace WorkSpaceApp.Features.Notes.Views;

internal sealed class SyncAdminSession : IDisposable
{
    private readonly ISyncService _sync;
    private readonly string       _folderPath;
    private readonly string       _token;
    private readonly string       _adminHwId;

    public SyncAdminSession(ISyncService sync, string folderPath, string token, string adminHwId)
    {
        _sync       = sync;
        _folderPath = folderPath;
        _token      = token;
        _adminHwId  = adminHwId;

        _sync.FileListRequested += OnFileListRequested;
        _sync.FileRequested     += OnFileRequested;
        _sync.FilePushed        += OnFilePushed;
        _sync.UserJoined        += OnUserJoined;
    }

    private void OnUserJoined(object? sender, UserJoinedArgs e)
    {
        if (e.Token != _token) return;
        System.Diagnostics.Debug.WriteLine(
            $"[SyncAdmin] User joined: hwId={e.HardwareId} role={e.Role}");
    }

    private async void OnFileListRequested(object? sender, FileListRequestedArgs e)
    {
        if (e.Token != _token) return;
        try
        {
            var files = Directory
                .GetFiles(_folderPath, "*.md", SearchOption.AllDirectories)
                .Select(f => Path.GetRelativePath(_folderPath, f).Replace('\\', '/'))
                .OrderBy(f => f)
                .ToArray();

            await _sync.SendFileListAsync(_token, e.UserConnectionId, files);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[SyncAdmin] FileList error: {ex.Message}");
        }
    }

    private async void OnFileRequested(object? sender, FileRequestedArgs e)
    {
        if (e.Token != _token) return;
        try
        {
            var fullPath = GetSafePath(e.RelativePath);
            if (fullPath is null || !File.Exists(fullPath)) return;

            var bytes  = await File.ReadAllBytesAsync(fullPath);
            var base64 = Convert.ToBase64String(bytes);
            await _sync.SendFileContentAsync(_token, e.UserConnectionId, e.RelativePath, base64);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[SyncAdmin] FileRequest error: {ex.Message}");
        }
    }

    private async void OnFilePushed(object? sender, FilePushedArgs e)
    {
        if (e.Token != _token) return;
        try
        {
            var fullPath = GetSafePath(e.RelativePath);
            if (fullPath is null) return;

            // Write the file
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            var bytes = Convert.FromBase64String(e.Base64Content);
            await File.WriteAllBytesAsync(fullPath, bytes);

            // Audit log in local DB
            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseSqlite($"Data Source={AppDbContext.GetDatabaseFilePath()}")
                .Options;
            await using var db = new AppDbContext(options);
            db.ChangeLogs.Add(new ChangeLog
            {
                ChangeType  = "Update",
                Description = $"File '{e.RelativePath}' pushed by remote user",
                ChangedBy   = e.UserHardwareId,
                ChangedAt   = DateTime.UtcNow
            });
            await db.SaveChangesAsync();

            await _sync.AcknowledgePushAsync(_token, e.UserConnectionId, true);

            System.Diagnostics.Debug.WriteLine(
                $"[SyncAdmin] File saved: {e.RelativePath} from {e.UserHardwareId}");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[SyncAdmin] FilePush error: {ex.Message}");
            await _sync.AcknowledgePushAsync(_token, e.UserConnectionId, false);
        }
    }

    /// <summary>Returns the absolute path only if it stays within _folderPath (prevents path traversal).</summary>
    private string? GetSafePath(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath)) return null;
        var safe     = relativePath.Replace('/', Path.DirectorySeparatorChar);
        var fullPath = Path.GetFullPath(Path.Combine(_folderPath, safe));
        return fullPath.StartsWith(_folderPath, StringComparison.OrdinalIgnoreCase)
            ? fullPath
            : null;
    }

    public void Dispose()
    {
        _sync.FileListRequested -= OnFileListRequested;
        _sync.FileRequested     -= OnFileRequested;
        _sync.FilePushed        -= OnFilePushed;
        _sync.UserJoined        -= OnUserJoined;
    }
}
