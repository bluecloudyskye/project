// ============================================================
// Core/Services/SignalRService.cs
// Business Rule: The client must maintain an ongoing SignalR connection
// to a virtual "owner server" to:
//   1. Report its own Online/Offline status + free disk space (Heartbeat).
//   2. Subscribe to the owner PC's status to know if file sync is possible.
//   3. Gate file-copy downloads behind a storage-limit check (1 GB).
// 
// Data Flow:
//   Client → Server: Heartbeat every 15s with hardware ID + disk space
//   Server → Client: OwnerStatusUpdate when owner goes online/offline
//   Client → Server: RequestFileDownload (gated by role + storage limit)
// ============================================================
using Microsoft.AspNetCore.SignalR.Client;
using WorkSpaceApp.Infrastructure.Data;

namespace WorkSpaceApp.Core.Services;

public interface ISignalRService
{
    bool IsConnected { get; }
    bool IsOwnerOnline { get; }

    /// <summary>Fires whenever the owner PC's status changes.</summary>
    event EventHandler<OwnerStatusChangedEventArgs>? OwnerStatusChanged;

    /// <summary>Fires whenever a peer's heartbeat is received.</summary>
    event EventHandler<HeartbeatEventArgs>? HeartbeatReceived;

    Task ConnectAsync(string serverUrl, string hardwareId, CancellationToken ct = default);
    Task DisconnectAsync();

    /// <summary>
    /// Business Rule: Initiate file download from the owner PC.
    /// Throws <see cref="StorageLimitExceededException"/> if local DB >= 1 GB.
    /// Throws <see cref="UnauthorizedAccessException"/> if the caller's role is Viewer.
    /// </summary>
    Task RequestFileDownloadAsync(Guid noteId, Role callerRole);
}

public sealed record OwnerStatusChangedEventArgs(bool IsOnline, long FreeDiskSpaceBytes);
public sealed record HeartbeatEventArgs(string HardwareId, bool IsOnline, long FreeDiskSpaceBytes, DateTime Timestamp);

// ── Re-export Role so callers don't need a separate using ──
public enum Role { Owner, Editor, Viewer }

/// <summary>
/// Manages the SignalR hub connection, heartbeat loop, and status subscriptions.
/// </summary>
public sealed class SignalRService : ISignalRService, IAsyncDisposable
{
    // ── Dependencies ──────────────────────────────────────────
    private readonly AppDbContext         _db;
    private readonly IHardwareIdService   _hwId;
    private readonly IStorageMonitorService _storage;

    // ── State ─────────────────────────────────────────────────
    private HubConnection? _hub;
    private Timer?         _heartbeatTimer;
    private string?        _connectedHardwareId;

    // ── Constants ─────────────────────────────────────────────
    /// <summary>How often the client pushes a heartbeat to the hub.</summary>
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(15);

    public bool IsConnected  => _hub?.State == HubConnectionState.Connected;
    public bool IsOwnerOnline { get; private set; }

    public event EventHandler<OwnerStatusChangedEventArgs>? OwnerStatusChanged;
    public event EventHandler<HeartbeatEventArgs>?          HeartbeatReceived;

    public SignalRService(AppDbContext db, IHardwareIdService hwId, IStorageMonitorService storage)
    {
        _db      = db;
        _hwId    = hwId;
        _storage = storage;
    }

    // ─────────────────────────────────────────────────────────
    /// <summary>
    /// Connects to the SignalR hub and starts the heartbeat loop.
    /// Business Rule: The connection carries the machine's HardwareId so
    /// the server can map sessions to registered users.
    /// </summary>
    public async Task ConnectAsync(string serverUrl, string hardwareId, CancellationToken ct = default)
    {
        if (IsConnected) return;

        _connectedHardwareId = hardwareId;

        _hub = new HubConnectionBuilder()
            .WithUrl(serverUrl, options =>
            {
                // Pass hardware ID as a query param for server-side identification
                options.Headers["X-Hardware-Id"] = hardwareId;
            })
            .WithAutomaticReconnect(new RetryPolicy())
            .Build();

        // ── Subscribe to server-pushed events ─────────────────

        // Business Rule: When the owner PC goes offline, sync must stop.
        _hub.On<bool, long>("OwnerStatusUpdate", (isOnline, freeDiskBytes) =>
        {
            IsOwnerOnline = isOnline;
            OwnerStatusChanged?.Invoke(this, new OwnerStatusChangedEventArgs(isOnline, freeDiskBytes));
        });

        // Receive heartbeats from other peers
        _hub.On<string, bool, long, DateTime>("PeerHeartbeat",
            (hwId, isOnline, freeDisk, ts) =>
                HeartbeatReceived?.Invoke(this, new HeartbeatEventArgs(hwId, isOnline, freeDisk, ts)));

        _hub.Reconnected += _ => SendHeartbeatAsync();
        _hub.Closed      += _ => { IsOwnerOnline = false; return Task.CompletedTask; };

        await _hub.StartAsync(ct);

        // Start periodic heartbeat
        _heartbeatTimer = new Timer(
            _ => _ = SendHeartbeatAsync(),
            null,
            TimeSpan.Zero,
            HeartbeatInterval);
    }

    // ─────────────────────────────────────────────────────────
    public async Task DisconnectAsync()
    {
        _heartbeatTimer?.Dispose();
        if (_hub is not null)
        {
            await _hub.StopAsync();
            await _hub.DisposeAsync();
            _hub = null;
        }
    }

    // ─────────────────────────────────────────────────────────
    /// <summary>
    /// Business Rule: Downloads are gated by:
    ///   (a) Role must be Owner or Editor – Viewers are blocked.
    ///   (b) Local storage must be < 1 GB.
    ///   (c) Owner PC must be online (SignalR connected).
    /// </summary>
    public async Task RequestFileDownloadAsync(Guid noteId, Role callerRole)
    {
        // Gate 1 – Role check
        if (callerRole == Role.Viewer)
            throw new UnauthorizedAccessException(
                "Viewer role does not permit file download. Contact the workspace Owner.");

        // Gate 2 – Storage limit (Business Rule: 1 GB cap)
        _db.EnforceStorageLimit();  // throws StorageLimitExceededException if >= 1 GB

        // Gate 3 – Owner must be online
        if (!IsOwnerOnline)
            throw new InvalidOperationException(
                "Owner PC is offline. File sync is unavailable until the owner reconnects.");

        if (_hub is null || !IsConnected)
            throw new InvalidOperationException("Not connected to the sync server.");

        // Invoke the hub method; server responds by streaming the file back
        await _hub.InvokeAsync("RequestFileDownload", noteId, _connectedHardwareId);
    }

    // ─────────────────────────────────────────────────────────
    /// <summary>
    /// Pushes heartbeat: HardwareId + online flag + free disk space.
    /// Business Rule: Free disk space is reported so the server can warn
    /// users approaching the 1 GB local storage limit.
    /// </summary>
    private async Task SendHeartbeatAsync()
    {
        if (_hub is null || !IsConnected) return;

        try
        {
            long freeDisk = _storage.GetFreeDiskSpaceBytes();
            await _hub.InvokeAsync(
                "Heartbeat",
                _connectedHardwareId,
                /*isOnline:*/ true,
                freeDisk,
                DateTime.UtcNow);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[SignalR] Heartbeat failed: {ex.Message}");
        }
    }

    public async ValueTask DisposeAsync() => await DisconnectAsync();

    // ── Retry policy: exponential back-off up to 60 s ────────
    private sealed class RetryPolicy : IRetryPolicy
    {
        private static readonly TimeSpan[] Delays =
            [TimeSpan.Zero, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30)];

        public TimeSpan? NextRetryDelay(RetryContext ctx) =>
            ctx.PreviousRetryCount < Delays.Length
                ? Delays[ctx.PreviousRetryCount]
                : TimeSpan.FromSeconds(60);
    }
}
