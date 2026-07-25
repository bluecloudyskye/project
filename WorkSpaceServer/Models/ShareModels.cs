namespace WorkSpaceServer.Models;

public enum ShareRole { Viewer, Editor }

public class ShareSession
{
    public string    Token             { get; init; } = GenerateToken();
    public string    AdminConnectionId { get; set;  } = "";
    public string    AdminHardwareId   { get; set;  } = "";
    public string    FolderName        { get; set;  } = "";
    public ShareRole Role              { get; set;  }
    public List<ConnectedUser> Users   { get; }      = [];
    public List<AuditEntry>   Audit    { get; }      = [];
    public DateTime CreatedAt          { get; }      = DateTime.UtcNow;

    private static string GenerateToken()
    {
        var bytes = new byte[4];
        System.Security.Cryptography.RandomNumberGenerator.Fill(bytes);
        return Convert.ToHexString(bytes); // 8 uppercase hex chars e.g. "A3F2C891"
    }
}

public record ConnectedUser(string ConnectionId, string HardwareId, DateTime JoinedAt);
public record AuditEntry(string HardwareId, string Action, DateTime At);
