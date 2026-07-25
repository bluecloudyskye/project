// ============================================================
// InProcessSyncServer.cs
// Hosts the SignalR relay hub inside the WinUI 3 process so
// users don't need a separate server binary.
// Auto-starts on http://0.0.0.0:5000 on first use.
// ============================================================
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;

namespace WorkSpaceApp;

// ── Models ─────────────────────────────────────────────────────
internal enum EmbeddedShareRole { Viewer, Editor }

internal class EmbeddedShareSession
{
    public string               Token             { get; init; } = GenerateToken();
    public string               AdminConnectionId { get; set; }  = "";
    public string               AdminHardwareId   { get; set; }  = "";
    public string               FolderName        { get; set; }  = "";
    public EmbeddedShareRole    Role              { get; set; }
    public List<EmbeddedConnectedUser> Users      { get; }       = [];
    public DateTime             CreatedAt         { get; }       = DateTime.UtcNow;

    private static string GenerateToken()
    {
        var bytes = new byte[4];
        System.Security.Cryptography.RandomNumberGenerator.Fill(bytes);
        return Convert.ToHexString(bytes);
    }
}

internal record EmbeddedConnectedUser(string ConnectionId, string HardwareId, DateTime JoinedAt);

// ── Session service ────────────────────────────────────────────
internal class EmbeddedSessionService
{
    private readonly ConcurrentDictionary<string, EmbeddedShareSession> _sessions = new();

    public EmbeddedShareSession Create(string adminConnId, string adminHwId,
                                       string folderName, EmbeddedShareRole role)
    {
        var s = new EmbeddedShareSession
        {
            AdminConnectionId = adminConnId,
            AdminHardwareId   = adminHwId,
            FolderName        = folderName,
            Role              = role
        };
        _sessions[s.Token] = s;
        return s;
    }

    public EmbeddedShareSession? GetByToken(string token) =>
        _sessions.TryGetValue(token, out var s) ? s : null;

    public IEnumerable<EmbeddedShareSession> GetByAdmin(string adminConnId) =>
        _sessions.Values.Where(s => s.AdminConnectionId == adminConnId);

    public EmbeddedShareSession? GetByUser(string userConnId) =>
        _sessions.Values.FirstOrDefault(s => s.Users.Any(u => u.ConnectionId == userConnId));

    public void Remove(string token) => _sessions.TryRemove(token, out _);
}

// ── SignalR hub ────────────────────────────────────────────────
internal class EmbeddedSyncHub : Hub
{
    private readonly EmbeddedSessionService _sessions;

    public EmbeddedSyncHub(EmbeddedSessionService sessions) => _sessions = sessions;

    public string CreateShare(string folderName, string role, string adminHwId)
    {
        var parsed = Enum.TryParse<EmbeddedShareRole>(role, true, out var r) ? r : EmbeddedShareRole.Viewer;
        var session = _sessions.Create(Context.ConnectionId, adminHwId, folderName, parsed);
        return session.Token;
    }

    public async Task SendFileList(string token, string userConnectionId, string[] files)
        => await Clients.Client(userConnectionId).SendAsync("OnFileListReceived", (object)files);

    public async Task SendFile(string token, string userConnectionId,
                               string relativePath, string base64Content)
        => await Clients.Client(userConnectionId).SendAsync("OnFileReceived", relativePath, base64Content);

    public async Task AcknowledgePush(string token, string userConnectionId, bool success)
        => await Clients.Client(userConnectionId).SendAsync("OnPushAcknowledged", success);

    public async Task JoinShare(string token, string userHwId)
    {
        var session = _sessions.GetByToken(token);
        if (session is null)
        {
            await Clients.Caller.SendAsync("OnJoinResult", "", "", "Token not found or expired.");
            return;
        }
        session.Users.Add(new EmbeddedConnectedUser(Context.ConnectionId, userHwId, DateTime.UtcNow));
        await Clients.Client(session.AdminConnectionId)
            .SendAsync("OnUserJoined", token, Context.ConnectionId, userHwId, session.Role.ToString());
        await Clients.Caller
            .SendAsync("OnJoinResult", session.Role.ToString(), session.FolderName, "");
    }

    public async Task RequestFileList(string token)
    {
        var session = GetValidatedUserSession(token);
        if (session is null) return;
        await Clients.Client(session.AdminConnectionId)
            .SendAsync("OnFileListRequested", token, Context.ConnectionId);
    }

    public async Task RequestFile(string token, string relativePath)
    {
        var session = GetValidatedUserSession(token);
        if (session is null) return;
        await Clients.Client(session.AdminConnectionId)
            .SendAsync("OnFileRequested", token, Context.ConnectionId, relativePath);
    }

    public async Task PushFile(string token, string relativePath,
                               string base64Content, string userHwId)
    {
        var session = GetValidatedUserSession(token);
        if (session is null) return;
        if (session.Role != EmbeddedShareRole.Editor)
        {
            await Clients.Caller.SendAsync("OnPermissionDenied",
                "You don't have permission to save files.");
            return;
        }
        await Clients.Client(session.AdminConnectionId)
            .SendAsync("OnFilePushed", token, Context.ConnectionId, relativePath, base64Content, userHwId);
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        foreach (var s in _sessions.GetByAdmin(Context.ConnectionId).ToList())
        {
            foreach (var u in s.Users)
                await Clients.Client(u.ConnectionId)
                    .SendAsync("OnAdminDisconnected", "Admin went offline. Session ended.");
            _sessions.Remove(s.Token);
        }
        var userSession = _sessions.GetByUser(Context.ConnectionId);
        if (userSession is not null)
        {
            userSession.Users.RemoveAll(u => u.ConnectionId == Context.ConnectionId);
            await Clients.Client(userSession.AdminConnectionId)
                .SendAsync("OnUserLeft", Context.ConnectionId);
        }
        await base.OnDisconnectedAsync(exception);
    }

    private EmbeddedShareSession? GetValidatedUserSession(string token)
    {
        var session = _sessions.GetByToken(token);
        if (session is null) return null;
        return session.Users.Any(u => u.ConnectionId == Context.ConnectionId) ? session : null;
    }
}

// ── Host launcher ──────────────────────────────────────────────
public static class InProcessSyncServer
{
    private static WebApplication? _app;
    private static readonly SemaphoreSlim _lock = new(1, 1);

    public const string DefaultUrl = "http://localhost:5000";

    public static bool IsRunning => _app != null;

    /// <summary>
    /// Starts the embedded relay server if it isn't already running.
    /// Safe to call multiple times — idempotent.
    /// </summary>
    public static async Task EnsureStartedAsync()
    {
        if (_app != null) return;
        await _lock.WaitAsync();
        try
        {
            if (_app != null) return;

            // Tell Kestrel to bind HTTP only on port 5000 before creating the builder.
            // Using the env-var is the most portable way to set URLs from a non-web SDK project.
            Environment.SetEnvironmentVariable("ASPNETCORE_URLS", "http://0.0.0.0:5000");

            var builder = WebApplication.CreateBuilder(new WebApplicationOptions
            {
                Args = [],
                ApplicationName = "WorkSpaceSync"
            });

            builder.Logging.ClearProviders();

            builder.Services.AddSignalR(opts =>
            {
                opts.MaximumReceiveMessageSize = 20 * 1024 * 1024;
            });
            builder.Services.AddSingleton<EmbeddedSessionService>();

            builder.Services.AddCors(opts =>
                opts.AddPolicy("AllowAll", p => p
                    .SetIsOriginAllowed(_ => true)
                    .AllowAnyMethod()
                    .AllowAnyHeader()
                    .AllowCredentials()));

            var app = builder.Build();
            app.UseCors("AllowAll");
            app.MapHub<EmbeddedSyncHub>("/sync");
            app.MapGet("/", () => "WorkSpace Sync Server running");

            await app.StartAsync();
            _app = app;
        }
        finally
        {
            _lock.Release();
        }
    }

    public static async Task StopAsync()
    {
        if (_app is null) return;
        await _app.StopAsync();
        await _app.DisposeAsync();
        _app = null;
    }
}
