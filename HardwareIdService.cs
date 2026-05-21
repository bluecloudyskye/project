// ============================================================
// Core/Services/HardwareIdService.cs
// Business Rule: User identity is bound to physical hardware.
// Combine Motherboard UUID + CPU ProcessorId → SHA-256 → HardwareId.
// This prevents account sharing / unauthorized machine access.
// ============================================================
using System.Management;
using System.Security.Cryptography;
using System.Text;

namespace WorkSpaceApp.Core.Services;

public interface IHardwareIdService
{
    /// <summary>
    /// Returns a stable, unique hardware ID for this machine.
    /// Business Rule: This ID is used as the primary user identifier
    /// for registration and role-based access control.
    /// </summary>
    string GetHardwareId();

    /// <summary>Returns raw motherboard serial / UUID (for diagnostics).</summary>
    string GetMotherboardSerial();

    /// <summary>Returns raw CPU ProcessorId (for diagnostics).</summary>
    string GetCpuProcessorId();
}

/// <summary>
/// Extracts hardware identifiers via WMI (System.Management).
/// Requires the <c>System.Management</c> NuGet package on .NET 6+.
/// </summary>
public sealed class HardwareIdService : IHardwareIdService
{
    // Cached on first call – hardware won't change during a session
    private string? _cachedId;

    /// <inheritdoc />
    public string GetHardwareId()
    {
        if (_cachedId is not null)
            return _cachedId;

        string motherboard = GetMotherboardSerial();
        string cpu         = GetCpuProcessorId();

        // Combine both identifiers so neither alone is sufficient to spoof
        string combined = $"{motherboard}|{cpu}";

        // SHA-256 → hex string (deterministic, non-reversible)
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(combined));
        _cachedId = Convert.ToHexString(hash).ToLowerInvariant();

        return _cachedId;
    }

    /// <inheritdoc />
    public string GetMotherboardSerial()
    {
        // Business Rule: Prefer UUID over SerialNumber; fall back gracefully
        // on VMs where one field may be empty / "To be filled by O.E.M."
        return QueryWmi(
            "SELECT UUID, SerialNumber FROM Win32_ComputerSystemProduct",
            row => row["SerialNumber"]?.ToString()
            ) ?? "UNKNOWN_MOTHERBOARD";
    }

    /// <inheritdoc />
    public string GetCpuProcessorId()
    {
        return QueryWmi(
            "SELECT ProcessorId FROM Win32_Processor",
            row => row["ProcessorId"]?.ToString()) ?? "UNKNOWN_CPU";
    }

    // ── Private helpers ───────────────────────────────────────

    /// <summary>
    /// Executes a WMI query and returns the first result transformed by <paramref name="selector"/>.
    /// Returns null when the query produces no rows or WMI is unavailable.
    /// </summary>
    private static string? QueryWmi(string query, Func<ManagementBaseObject, string?> selector)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(query);
            using ManagementObjectCollection results = searcher.Get();

            foreach (ManagementBaseObject obj in results)
            {
                using (obj)
                {
                    string? value = selector(obj);
                    if (IsUsable(value))
                        return value!.Trim();
                }
            }
        }
        catch (Exception ex)
        {
            // WMI may be restricted in some enterprise environments.
            // Log and fall back – the caller will use "UNKNOWN_*" tokens.
            System.Diagnostics.Debug.WriteLine($"[HardwareIdService] WMI query failed: {ex.Message}");
        }

        return null;
    }

    /// <summary>
    /// Returns true if the string is non-empty and not a known placeholder value
    /// that OEMs use when the field is unprogrammed.
    /// </summary>
    private static bool IsUsable(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;

        // Common OEM placeholder strings
        string[] placeholders =
        [
            "To Be Filled By O.E.M.",
            "Default string",
            "Not Applicable",
            "00000000-0000-0000-0000-000000000000",
            "FFFFFFFF-FFFF-FFFF-FFFF-FFFFFFFFFFFF",
            "None",
            "N/A"
        ];

        string trimmed = value.Trim();
        return !placeholders.Any(p => p.Equals(trimmed, StringComparison.OrdinalIgnoreCase));
    }
}
