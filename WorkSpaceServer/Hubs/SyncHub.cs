using Microsoft.AspNetCore.SignalR;
using WorkSpaceServer.Models;
using WorkSpaceServer.Services;

namespace WorkSpaceServer.Hubs;

public class SyncHub : Hub
{
    private readonly SessionService _sessions;
    private readonly ILogger<SyncHub> _log;

    public SyncHub(SessionService sessions, ILogger<SyncHub> log)
    {
        _sessions = sessions;
        _log      = log;
    }

    // ── Admin methods ──────────────────────────────────────────

    /// <summary>Admin calls this to create a share session. Returns the token.</summary>
    public string CreateShare(string folderName, string role, string adminHwId)
    {
        var parsed = Enum.TryParse<ShareRole>(role, true, out var r) ? r : ShareRole.Viewer;
        var session = _sessions.Create(Context.ConnectionId, adminHwId, folderName, parsed);
        _log.LogInformation("Share created: token={Token} folder={Folder} role={Role} admin={HwId}",
            session.Token, folderName, role, adminHwId);
        return session.Token;
    }

    /// <summary>Admin sends the file list in response to OnFileListRequested.</summary>
    public async Task SendFileList(string token, string userConnectionId, string[] files)
    {
        await Clients.Client(userConnectionId).SendAsync("OnFileListReceived", (object)files);
    }

    /// <summary>Admin sends file content in response to OnFileRequested.</summary>
    public async Task SendFile(string token, string userConnectionId, string relativePath, string base64Content)
    {
        await Clients.Client(userConnectionId).SendAsync("OnFileReceived", relativePath, base64Content);
    }

    /// <summary>Admin acknowledges a file push.</summary>
    public async Task AcknowledgePush(string token, string userConnectionId, bool success)
    {
        await Clients.Client(userConnectionId).SendAsync("OnPushAcknowledged", success);
    }

    // ── User methods ───────────────────────────────────────────

    /// <summary>User joins a share session by token. Server responds with OnJoinResult.</summary>
    public async Task JoinShare(string token, string userHwId)
    {
        var session = _sessions.GetByToken(token);
        if (session is null)
        {
            await Clients.Caller.SendAsync("OnJoinResult", "", "", "Token not found or expired.");
            return;
        }

        session.Users.Add(new ConnectedUser(Context.ConnectionId, userHwId, DateTime.UtcNow));
        _sessions.Log(token, userHwId, "Joined");

        _log.LogInformation("User joined: token={Token} user={HwId}", token, userHwId);

        await Clients.Client(session.AdminConnectionId)
            .SendAsync("OnUserJoined", token, Context.ConnectionId, userHwId, session.Role.ToString());

        await Clients.Caller
            .SendAsync("OnJoinResult", session.Role.ToString(), session.FolderName, "");
    }

    /// <summary>User requests the file list for a share token.</summary>
    public async Task RequestFileList(string token)
    {
        var session = GetValidatedUserSession(token);
        if (session is null) return;

        await Clients.Client(session.AdminConnectionId)
            .SendAsync("OnFileListRequested", token, Context.ConnectionId);
    }

    /// <summary>User requests the content of a specific file.</summary>
    public async Task RequestFile(string token, string relativePath)
    {
        var session = GetValidatedUserSession(token);
        if (session is null) return;

        await Clients.Client(session.AdminConnectionId)
            .SendAsync("OnFileRequested", token, Context.ConnectionId, relativePath);
    }

    /// <summary>
    /// Editor pushes a modified file back to admin.
    /// Server enforces the role — Viewer attempts are rejected here.
    /// </summary>
    public async Task PushFile(string token, string relativePath, string base64Content, string userHwId)
    {
        var session = GetValidatedUserSession(token);
        if (session is null) return;

        if (session.Role != ShareRole.Editor)
        {
            await Clients.Caller.SendAsync("OnPermissionDenied", "You don't have permission to save files.");
            return;
        }

        _sessions.Log(token, userHwId, $"PushFile:{relativePath}");
        _log.LogInformation("File pushed: token={Token} path={Path} user={HwId}", token, relativePath, userHwId);

        await Clients.Client(session.AdminConnectionId)
            .SendAsync("OnFilePushed", token, Context.ConnectionId, relativePath, base64Content, userHwId);
    }

    // ── Disconnect cleanup ────────────────────────────────────

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        // If admin disconnected → remove session, notify all users
        foreach (var s in _sessions.GetByAdmin(Context.ConnectionId).ToList())
        {
            foreach (var u in s.Users)
                await Clients.Client(u.ConnectionId).SendAsync("OnAdminDisconnected", "Admin went offline. Session ended.");
            _sessions.Remove(s.Token);
            _log.LogInformation("Admin disconnected, session removed: token={Token}", s.Token);
        }

        // If user disconnected → notify admin
        var userSession = _sessions.GetByUser(Context.ConnectionId);
        if (userSession is not null)
        {
            userSession.Users.RemoveAll(u => u.ConnectionId == Context.ConnectionId);
            await Clients.Client(userSession.AdminConnectionId)
                .SendAsync("OnUserLeft", Context.ConnectionId);
        }

        await base.OnDisconnectedAsync(exception);
    }

    // ── Helpers ───────────────────────────────────────────────

    private ShareSession? GetValidatedUserSession(string token)
    {
        var session = _sessions.GetByToken(token);
        if (session is null) return null;
        return session.Users.Any(u => u.ConnectionId == Context.ConnectionId) ? session : null;
    }
}
