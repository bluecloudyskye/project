// ============================================================
// Core/Services/UserProfileService.cs
// Manages the local user's public display name.
// Persists to HardwareRegistration in SQLite; cached in memory.
// ============================================================
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using WorkSpaceApp.Core.Models;
using WorkSpaceApp.Infrastructure.Data;

namespace WorkSpaceApp.Core.Services;

public interface IUserProfileService
{
    string DisplayName { get; }

    /// <summary>Fires on the calling thread when the display name changes.</summary>
    event EventHandler<string>? DisplayNameChanged;

    Task LoadAsync();
    Task UpdateDisplayNameAsync(string name);
}

public sealed class UserProfileService : IUserProfileService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IHardwareIdService   _hwId;

    public string DisplayName { get; private set; } = "You";

    public event EventHandler<string>? DisplayNameChanged;

    public UserProfileService(IServiceScopeFactory scopeFactory, IHardwareIdService hwId)
    {
        _scopeFactory = scopeFactory;
        _hwId         = hwId;
    }

    /// <summary>Loads the stored display name from DB on startup.</summary>
    public async Task LoadAsync()
    {
        using var scope = _scopeFactory.CreateScope();
        var db    = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var hwId  = _hwId.GetHardwareId();

        var reg = await db.HardwareRegistrations
            .FirstOrDefaultAsync(r => r.HardwareId == hwId);

        if (reg is not null && !string.IsNullOrWhiteSpace(reg.DisplayName))
            DisplayName = reg.DisplayName;
    }

    /// <summary>Persists a new display name and fires DisplayNameChanged.</summary>
    public async Task UpdateDisplayNameAsync(string name)
    {
        name = name.Trim();
        if (string.IsNullOrWhiteSpace(name)) return;

        using var scope = _scopeFactory.CreateScope();
        var db    = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var hwId  = _hwId.GetHardwareId();

        var reg = await db.HardwareRegistrations
            .FirstOrDefaultAsync(r => r.HardwareId == hwId);

        if (reg is null)
        {
            reg = new HardwareRegistration { HardwareId = hwId, DisplayName = name };
            db.HardwareRegistrations.Add(reg);
        }
        else
        {
            reg.DisplayName = name;
        }

        await db.SaveChangesAsync();
        DisplayName = name;
        DisplayNameChanged?.Invoke(this, name);
    }
}
