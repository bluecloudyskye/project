// ============================================================
// Core/Services/SyncService.cs
// Wraps a SignalR hub connection for folder-share relay.
//
// Link format:  wsync://<TOKEN>@<HOST>:<PORT>
// Example:      wsync://A3F2C891@192.168.1.100:5000
//
// Admin flow:
//   1. ConnectAsync(serverUrl)
//   2. CreateShareAsync(folderName, role, hwId) → token → build link
//   3. Serve incoming FileListRequested / FileRequested / FilePushed events
//
// User flow:
//   1. ParseLink(link) → (serverUrl, token)
//   2. ConnectAsync(serverUrl)
//   3. JoinShareAsync(token, hwId) → (role, folderName)
//   4. RequestFileListAsync / RequestFileContentAsync / PushFileAsync
// ============================================================
using Microsoft.AspNetCore.SignalR.Client;

namespace WorkSpaceApp.Core.Services;

// ── Event argument records ─────────────────────────────────────
public record UserJoinedArgs(string Token, string UserConnectionId, string HardwareId, string Role);
public record FileListRequestedArgs(string Token, string UserConnectionId);
public record FileRequestedArgs(string Token, string UserConnectionId, string RelativePath);
public record FilePushedArgs(string Token, string UserConnectionId, string RelativePath, string Base64Content, string UserHardwareId);
public record JoinResultArgs(string Role, string FolderName, string Error);
public record FileReceivedArgs(string RelativePath, string Base64Content);

// ── Interface ──────────────────────────────────────────────────
public interface ISyncService : IAsyncDisposable
{
    bool IsConnected { get; }

    // Connection
    Task ConnectAsync(string serverUrl);
    Task DisconnectAsync();

    // Admin operations
    Task<string> CreateShareAsync(string folderName, string role, string adminHwId);
    Task SendFileListAsync(string token, string userConnectionId, string[] files);
    Task SendFileContentAsync(string token, string userConnectionId, string relativePath, string base64Content);
    Task AcknowledgePushAsync(string token, string userConnectionId, bool success);

    // User operations
    Task<JoinResultArgs?> JoinShareAsync(string token, string userHwId);
    Task RequestFileListAsync(string token);
    Task<string?> RequestFileContentAsync(string token, string relativePath);
    Task PushFileAsync(string token, string relativePath, string base64Content, string userHwId);

    // Admin receives these
    event EventHandler<UserJoinedArgs>?       UserJoined;
    event EventHandler<FileListRequestedArgs>? FileListRequested;
    event EventHandler<FileRequestedArgs>?    FileRequested;
    event EventHandler<FilePushedArgs>?       FilePushed;
    event EventHandler<string>?               UserLeft;

    // User receives these
    event EventHandler<string[]>?             FileListReceived;
    event EventHandler<FileReceivedArgs>?     FileReceived;
    event EventHandler<bool>?                 PushAcknowledged;
    event EventHandler<string>?               PermissionDenied;
    event EventHandler<string>?               AdminDisconnected;

    // Helper
    static (string ServerUrl, string Token)? ParseLink(string link)
    {
        // Accept both "wsync://TOKEN@host:port" and bare token (for localhost default)
        if (link.StartsWith("wsync://", StringComparison.OrdinalIgnoreCase))
        {
            var body  = link["wsync://".Length..];
            var atIdx = body.IndexOf('@');
            if (atIdx < 0) return null;
            var token     = body[..atIdx].ToUpperInvariant();
            var hostPart  = body[(atIdx + 1)..];
            var serverUrl = hostPart.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                ? hostPart
                : $"http://{hostPart}";
            return (serverUrl, token);
        }
        // Bare 8-char hex token → assume localhost
        if (link.Length == 8 && link.All(c => "0123456789ABCDEFabcdef".Contains(c)))
            return ("http://localhost:5000", link.ToUpperInvariant());
        return null;
    }

    static string BuildLink(string serverUrl, string token)
    {
        var host = serverUrl
            .Replace("http://", "", StringComparison.OrdinalIgnoreCase)
            .Replace("https://", "", StringComparison.OrdinalIgnoreCase)
            .TrimEnd('/');
        return $"wsync://{token}@{host}";
    }
}

// ── Implementation ─────────────────────────────────────────────
public sealed class SyncService : ISyncService
{
    private HubConnection? _hub;

    public bool IsConnected => _hub?.State == HubConnectionState.Connected;

    // Admin events
    public event EventHandler<UserJoinedArgs>?        UserJoined;
    public event EventHandler<FileListRequestedArgs>?  FileListRequested;
    public event EventHandler<FileRequestedArgs>?     FileRequested;
    public event EventHandler<FilePushedArgs>?        FilePushed;
    public event EventHandler<string>?                UserLeft;

    // User events
    public event EventHandler<string[]>?              FileListReceived;
    public event EventHandler<FileReceivedArgs>?      FileReceived;
    public event EventHandler<bool>?                  PushAcknowledged;
    public event EventHandler<string>?                PermissionDenied;
    public event EventHandler<string>?                AdminDisconnected;

    public async Task ConnectAsync(string serverUrl)
    {
        if (IsConnected) return;

        var url = serverUrl.TrimEnd('/') + "/sync";
        _hub = new HubConnectionBuilder()
            .WithUrl(url)
            .WithAutomaticReconnect()
            .Build();

        // ── Register server→client callbacks ──────────────────

        _hub.On<string, string, string, string>("OnUserJoined",
            (token, connId, hwId, role) =>
                UserJoined?.Invoke(this, new UserJoinedArgs(token, connId, hwId, role)));

        _hub.On<string, string>("OnFileListRequested",
            (token, connId) =>
                FileListRequested?.Invoke(this, new FileListRequestedArgs(token, connId)));

        _hub.On<string, string, string>("OnFileRequested",
            (token, connId, path) =>
                FileRequested?.Invoke(this, new FileRequestedArgs(token, connId, path)));

        _hub.On<string, string, string, string, string>("OnFilePushed",
            (token, connId, path, content, hwId) =>
                FilePushed?.Invoke(this, new FilePushedArgs(token, connId, path, content, hwId)));

        _hub.On<string>("OnUserLeft",
            connId => UserLeft?.Invoke(this, connId));

        _hub.On<object>("OnFileListReceived", raw =>
        {
            var files = raw switch
            {
                string[] arr  => arr,
                System.Text.Json.JsonElement je when je.ValueKind == System.Text.Json.JsonValueKind.Array
                    => je.EnumerateArray().Select(e => e.GetString() ?? "").ToArray(),
                _ => []
            };
            FileListReceived?.Invoke(this, files);
        });

        _hub.On<string, string>("OnFileReceived",
            (path, content) =>
                FileReceived?.Invoke(this, new FileReceivedArgs(path, content)));

        _hub.On<bool>("OnPushAcknowledged",
            success => PushAcknowledged?.Invoke(this, success));

        _hub.On<string>("OnPermissionDenied",
            msg => PermissionDenied?.Invoke(this, msg));

        _hub.On<string>("OnAdminDisconnected",
            msg => AdminDisconnected?.Invoke(this, msg));

        _hub.On<string, string, string>("OnJoinResult", (role, folderName, error) =>
            _pendingJoin?.TrySetResult(new JoinResultArgs(role, folderName, error)));

        await _hub.StartAsync();
    }

    public async Task DisconnectAsync()
    {
        if (_hub is null) return;
        await _hub.StopAsync();
        await _hub.DisposeAsync();
        _hub = null;
    }

    // ── Admin invocations ──────────────────────────────────────

    public async Task<string> CreateShareAsync(string folderName, string role, string adminHwId)
    {
        if (_hub is null) throw new InvalidOperationException("Not connected to sync server.");
        return await _hub.InvokeAsync<string>("CreateShare", folderName, role, adminHwId);
    }

    public async Task SendFileListAsync(string token, string userConnectionId, string[] files)
    {
        if (_hub is null) return;
        await _hub.InvokeAsync("SendFileList", token, userConnectionId, files);
    }

    public async Task SendFileContentAsync(string token, string userConnectionId, string relativePath, string base64Content)
    {
        if (_hub is null) return;
        await _hub.InvokeAsync("SendFile", token, userConnectionId, relativePath, base64Content);
    }

    public async Task AcknowledgePushAsync(string token, string userConnectionId, bool success)
    {
        if (_hub is null) return;
        await _hub.InvokeAsync("AcknowledgePush", token, userConnectionId, success);
    }

    // ── User invocations ───────────────────────────────────────

    private TaskCompletionSource<JoinResultArgs>? _pendingJoin;

    public async Task<JoinResultArgs?> JoinShareAsync(string token, string userHwId)
    {
        if (_hub is null) throw new InvalidOperationException("Not connected to sync server.");
        _pendingJoin = new TaskCompletionSource<JoinResultArgs>();
        await _hub.InvokeAsync("JoinShare", token, userHwId);
        try   { return await _pendingJoin.Task.WaitAsync(TimeSpan.FromSeconds(10)); }
        catch { return null; }
        finally { _pendingJoin = null; }
    }

    public async Task RequestFileListAsync(string token)
    {
        if (_hub is null) return;
        await _hub.InvokeAsync("RequestFileList", token);
    }

    private readonly Dictionary<string, TaskCompletionSource<string?>> _pendingFiles = new();

    public async Task<string?> RequestFileContentAsync(string token, string relativePath)
    {
        if (_hub is null) return null;

        var tcs = new TaskCompletionSource<string?>();
        _pendingFiles[relativePath] = tcs;

        void Handler(object? _, FileReceivedArgs args)
        {
            if (!_pendingFiles.TryGetValue(args.RelativePath, out var pending)) return;
            _pendingFiles.Remove(args.RelativePath);
            FileReceived -= Handler;
            pending.TrySetResult(args.Base64Content);
        }
        FileReceived += Handler;

        await _hub.InvokeAsync("RequestFile", token, relativePath);

        try   { return await tcs.Task.WaitAsync(TimeSpan.FromSeconds(15)); }
        catch
        {
            _pendingFiles.Remove(relativePath);
            FileReceived -= Handler;
            return null;
        }
    }

    public async Task PushFileAsync(string token, string relativePath, string base64Content, string userHwId)
    {
        if (_hub is null) return;
        await _hub.InvokeAsync("PushFile", token, relativePath, base64Content, userHwId);
    }

    public async ValueTask DisposeAsync() => await DisconnectAsync();
}
