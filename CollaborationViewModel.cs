// ============================================================
// Features/Sync/ViewModels/CollaborationViewModel.cs
// ViewModel for the Collaboration / Sync settings page.
// Business Rules enforced:
//   1. File download blocked for Viewers.
//   2. Download blocked when local DB >= 1 GB.
//   3. Sync disabled when owner PC is offline.
// ============================================================
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.ObjectModel;
using WorkSpaceApp.Core.Services;
using WorkSpaceApp.Infrastructure.Data;
using Microsoft.Extensions.DependencyInjection;

namespace WorkSpaceApp.Features.Sync.ViewModels;

public partial class TeamMemberItem : ObservableObject
{
    public string HardwareId  { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public string Email       { get; init; } = string.Empty;
    public string Initials    => string.Concat(DisplayName.Split(' ').Select(p => p[0].ToString().ToUpper()));

    [ObservableProperty] private string _role = "Viewer";

    public bool IsNotOwner => Role != "Owner";

    public List<string> RoleOptions { get; } = ["Owner", "Editor", "Viewer"];
}

public partial class CollaborationViewModel : ObservableObject
{
    private readonly ISignalRService        _signalR;
    private readonly IStorageMonitorService _storage;
    private readonly AppDbContext           _db;

    // ── Connection state ──────────────────────────────────────

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ConnectionButtonLabel))]
    [NotifyPropertyChangedFor(nameof(CanSync))]
    private bool _isConnected = false;

    public string ConnectionButtonLabel => IsConnected ? "Connected" : "Reconnect";

    // ── Share link ────────────────────────────────────────────

    [ObservableProperty]
    private string _shareLink = "https://workspace.app/share/placeholder";

    [ObservableProperty]
    private string _selectedLinkAccess = "Viewer";

    public List<string> LinkAccessOptions { get; } = ["Viewer", "Editor"];

    // ── Team members ──────────────────────────────────────────

    public ObservableCollection<TeamMemberItem> TeamMembers { get; } = [];

    // ── Sync state ────────────────────────────────────────────

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSync))]
    private bool _isStorageWarning = false;

    [ObservableProperty]
    private string _syncStatusMessage = "Last synced: never";

    /// <summary>
    /// Business Rule: Sync only allowed when online AND storage < 1 GB.
    /// </summary>
    public bool CanSync => IsConnected && !_storage.IsLimitReached;

    public CollaborationViewModel(ISignalRService signalR, IStorageMonitorService storage, AppDbContext db)
    {
        _signalR = signalR;
        _storage  = storage;
        _db       = db;

        _signalR.OwnerStatusChanged += (_, e) =>
        {
            IsConnected = e.IsOnline;
            OnPropertyChanged(nameof(CanSync));
        };

        IsConnected     = _signalR.IsConnected;
        IsStorageWarning = _storage.UsageRatio > 0.8;

        // Seed demo members (replace with DB query in production)
        TeamMembers.Add(new TeamMemberItem { DisplayName = "Ilya B", Email = "IlyaB@mail.ru", Role = "Owner" });
        TeamMembers.Add(new TeamMemberItem { DisplayName = "Nikita P",  Email = "NikitaP@outlook.com",  Role = "Editor" });
        TeamMembers.Add(new TeamMemberItem { DisplayName = "Gleb S",   Email = "GlebS@yandex.ru", Role = "Viewer" });
    }

    [RelayCommand]
    public async Task ToggleConnectionAsync()
    {
        if (IsConnected)
            await _signalR.DisconnectAsync();
        else
        {
            string hwId = App.Services.GetRequiredService<IHardwareIdService>().GetHardwareId();
            await _signalR.ConnectAsync("https://your-signalr-server/workspacehub", hwId);
        }
        IsConnected = _signalR.IsConnected;
    }

    [RelayCommand]
    public void CopyLink()
    {
        var dataPackage = new Windows.ApplicationModel.DataTransfer.DataPackage();
        dataPackage.SetText(ShareLink);
        Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(dataPackage);
    }

    [RelayCommand]
    public async Task SyncNowAsync()
    {
        try
        {
            // Business Rule: storage check is inside RequestFileDownloadAsync
            // Here we just demonstrate calling it with the current user's role
            var myRole = Core.Services.Role.Editor; // resolve from UserRole in production
            await _signalR.RequestFileDownloadAsync(Guid.Empty, myRole);
            SyncStatusMessage = $"Last synced: {DateTime.Now:HH:mm}";
        }
        catch (StorageLimitExceededException ex)
        {
            SyncStatusMessage = $"Sync blocked: {ex.Message}";
            IsStorageWarning  = true;
        }
        catch (Exception ex)
        {
            SyncStatusMessage = $"Sync failed: {ex.Message}";
        }
    }

    [RelayCommand]
    public void InviteMember()
    {
        // Open invite dialog – implemented in code-behind with ContentDialog
    }

    [RelayCommand]
    public void RemoveMember(TeamMemberItem member) => TeamMembers.Remove(member);
}
