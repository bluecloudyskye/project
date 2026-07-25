using System.Collections.Concurrent;
using WorkSpaceServer.Models;

namespace WorkSpaceServer.Services;

public class SessionService
{
    private readonly ConcurrentDictionary<string, ShareSession> _sessions = new();

    public ShareSession Create(string adminConnectionId, string adminHwId, string folderName, ShareRole role)
    {
        var s = new ShareSession
        {
            AdminConnectionId = adminConnectionId,
            AdminHardwareId   = adminHwId,
            FolderName        = folderName,
            Role              = role
        };
        _sessions[s.Token] = s;
        return s;
    }

    public ShareSession? GetByToken(string token) =>
        _sessions.TryGetValue(token, out var s) ? s : null;

    public IEnumerable<ShareSession> GetByAdmin(string adminConnectionId) =>
        _sessions.Values.Where(s => s.AdminConnectionId == adminConnectionId);

    public ShareSession? GetByUser(string userConnectionId) =>
        _sessions.Values.FirstOrDefault(s => s.Users.Any(u => u.ConnectionId == userConnectionId));

    public void Remove(string token) => _sessions.TryRemove(token, out _);

    public void Log(string token, string hwId, string action)
    {
        if (_sessions.TryGetValue(token, out var s))
            s.Audit.Add(new AuditEntry(hwId, action, DateTime.UtcNow));
    }
}
