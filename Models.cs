// ============================================================
// Core/Models/Note.cs
// Business Rule: A Note owns a rich-text body (stored as HTML),
// belongs to folders via Tags, and records every mutation in ChangeLogs.
// 
// Data Flow:
//   User creates note → Note entity saved to DB with OwnerHardwareId
//   Note edited → ContentMarkdown updated + ChangeLog entry created
//   Sync operation → Notes transferred between devices via SignalR
// ============================================================
using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace WorkSpaceApp.Core.Models;

/// <summary>
/// Represents a rich-text note document.
/// Business Rule: Notes are scoped to a registered Hardware-ID owner
/// and can be shared with other users via UserRole entries.
/// </summary>
public class Note
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Title { get; set; } = "Untitled Note";

    /// <summary>
    /// Rich-text body stored as HTML (from WinUI RichEditBox / WebView2 Quill).
    /// Business Rule: Content must be persisted in HTML to allow PDF export via QuestPDF.
    /// </summary>
    public string? ContentMarkdown { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Hardware UUID of the owner's machine (from HardwareIdService).</summary>
    [Required, MaxLength(256)]
    public string OwnerHardwareId { get; set; } = string.Empty;

    // Navigation
    public ICollection<NoteTag> NoteTags { get; set; } = new List<NoteTag>();
    public ICollection<ChangeLog> ChangeLogs { get; set; } = new List<ChangeLog>();
    public ICollection<UserRole> UserRoles { get; set; } = new List<UserRole>();
}

// ============================================================
// Core/Models/Tag.cs
// ============================================================
/// <summary>Tag used for organizing notes and database entries.</summary>
public class Tag
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    [Required, MaxLength(128)]
    public string Name { get; set; } = string.Empty;

    /// <summary>Hex color for the tag dot (e.g. "#ff4757").</summary>
    [MaxLength(9)]
    public string Color { get; set; } = "#0067c0";

    // Navigation
    public ICollection<NoteTag> NoteTags { get; set; } = new List<NoteTag>();

    public ICollection<FileTag> FileTags { get; set; } = new List<FileTag>();
}

// ============================================================
// Core/Models/NoteTag.cs  (join table)
// ============================================================
/// <summary>Many-to-many join between Note and Tag.</summary>
public class NoteTag
{
    public Guid NoteId { get; set; }
    public Note Note { get; set; } = null!;

    public Guid TagId { get; set; }
    public Tag Tag { get; set; } = null!;
}

// ============================================================
// Core/Models/DynamicTable.cs
// Business Rule: Users can create custom tables at runtime.
// The schema is stored as JSON; actual row data lives in DynamicRow.
// ============================================================
/// <summary>
/// Metadata for a user-defined table.
/// Business Rule: Every schema change is appended to ChangeLogs with
/// ChangeType = "SchemaChange" so an audit trail is maintained.
/// </summary>
public class DynamicTable
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    [Required, MaxLength(256)]
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// JSON array describing columns: [{ "name": "Title", "type": "Text" }, ...]
    /// Supported types: Text | Number | Date | Boolean | Select
    /// </summary>
    [Required]
    public string SchemaJson { get; set; } = "[]";

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    [Required, MaxLength(256)]
    public string OwnerHardwareId { get; set; } = string.Empty;

    // Navigation
    public ICollection<DynamicRow> Rows { get; set; } = new List<DynamicRow>();
    public ICollection<ChangeLog> ChangeLogs { get; set; } = new List<ChangeLog>();
}

// ============================================================
// Core/Models/DynamicRow.cs
// ============================================================
/// <summary>A single data row for a DynamicTable, stored as JSON.</summary>
public class DynamicRow
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid DynamicTableId { get; set; }
    public DynamicTable DynamicTable { get; set; } = null!;

    /// <summary>Row values serialized as JSON object: { "columnName": value, ... }</summary>
    [Required]
    public string DataJson { get; set; } = "{}";

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    [MaxLength(256)]
    public string? AssignedUser { get; set; }

    public EntryStatus Status { get; set; } = EntryStatus.Pending;
}

public enum EntryStatus { Pending, InProgress, Completed }

// ============================================================
// Core/Models/ChangeLog.cs
// Business Rule: Every Create / Update / Delete on Notes and
// DynamicTables must produce an immutable ChangeLog record.
// This satisfies the "Audit Trail" requirement.
// ============================================================
/// <summary>
/// Immutable audit record. Never delete or update rows in this table.
/// </summary>
public class ChangeLog
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>
    /// Type of change: "Create" | "Update" | "Delete" | "SchemaChange" | "TagAdded" | "TagRemoved"
    /// </summary>
    [Required, MaxLength(64)]
    public string ChangeType { get; set; } = string.Empty;

    /// <summary>Human-readable description of what changed.</summary>
    [MaxLength(1024)]
    public string? Description { get; set; }

    /// <summary>Snapshot of the previous state (JSON) for diff rendering.</summary>
    public string? PreviousValueJson { get; set; }

    public DateTime ChangedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Display name of the user who made the change.</summary>
    [MaxLength(256)]
    public string ChangedBy { get; set; } = "You";

    // Optional FK to Note (nullable – also used for DynamicTable changes)
    public Guid? NoteId { get; set; }
    public Note? Note { get; set; }

    // Optional FK to DynamicTable
    public Guid? DynamicTableId { get; set; }
    public DynamicTable? DynamicTable { get; set; }
}

// ============================================================
// Core/Models/UserRole.cs
// Business Rule: Role-Based Access Control – Owner can sync/download,
// Editor can edit, Viewer is read-only.  Hardware-UUID identifies users.
// ============================================================
/// <summary>
/// Associates a hardware-identified user with a permission role on a Note.
/// Business Rule: File-copy download via SignalR is only permitted for
/// Owner and Editor roles. Viewers are blocked at the sync layer.
/// </summary>
public class UserRole
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Hardware UUID of the participant machine.</summary>
    [Required, MaxLength(256)]
    public string HardwareId { get; set; } = string.Empty;

    /// <summary>Friendly display name (set during hardware registration).</summary>
    [MaxLength(256)]
    public string DisplayName { get; set; } = string.Empty;

    [MaxLength(256)]
    public string? Email { get; set; }

    public Role Role { get; set; } = Role.Viewer;

    public Guid NoteId { get; set; }
    public Note Note { get; set; } = null!;

    public DateTime GrantedAt { get; set; } = DateTime.UtcNow;
}

public enum Role { Owner, Editor, Viewer }

// ============================================================
// Core/Models/FileTag.cs
// Business Rule: Associates tags with file paths (for .md files).
// This allows tagging files that exist on the filesystem.
// ============================================================
/// <summary>Many-to-many join between File path and Tag.</summary>
public class FileTag
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Full path to the file on the filesystem.</summary>
    [Required, MaxLength(512)]
    public string FilePath { get; set; } = string.Empty;

    public Guid TagId { get; set; }
    public Tag Tag { get; set; } = null!;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

// ============================================================
// Core/Models/HardwareRegistration.cs
// Business Rule: Each client must register its Hardware UUID once.
// Subsequent launches verify registration; unregistered machines
// cannot access shared resources.
// ============================================================
/// <summary>Stores the local machine's registration entry.</summary>
public class HardwareRegistration
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    [Required, MaxLength(256)]
    public string HardwareId { get; set; } = string.Empty;

    [Required, MaxLength(256)]
    public string DisplayName { get; set; } = string.Empty;

    [MaxLength(256)]
    public string? Email { get; set; }

    public DateTime RegisteredAt { get; set; } = DateTime.UtcNow;

    public bool IsActive { get; set; } = true;
}
