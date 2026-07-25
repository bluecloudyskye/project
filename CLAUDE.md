# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Build & Run

```powershell
# Build (Debug, x64)
dotnet build WorkSpaceApp.sln -c Debug -p:Platform=x64

# Run
dotnet run --project WorkSpaceApp.csproj

# Publish self-contained x64 exe
dotnet publish WorkSpaceApp.csproj -c Release -r win-x64 --self-contained
```

The app is WinUI 3 / WindowsAppSDK — it requires Windows 10 1809+ and must run on Windows. There is no test project.

## Architecture

**Pattern:** MVVM + feature-slice layout, with a flat physical file structure (all source files are in the project root rather than subdirectories — the `PROJECT_STRUCTURE.md` shows the *intended* layout, but files have not yet been moved).

**DI container** (`App.xaml.cs → BuildHost`): `Microsoft.Extensions.Hosting` wires everything. `AppDbContext` is scoped, services are singletons, ViewModels are transient. Resolve services via `App.Services`.

**Shell** (`MainWindow.xaml.cs`): Single `NavigationView` with a `Frame` (ContentFrame). Navigation is tag-driven: `"Dashboard"` → `NoteEditorPage`, `"Database"` → `DatabasePage`, `"Reminders"` → `RemindersPage`. Tags prefixed `"Folder_"` or `"Tag_"` are handled specially to load folder trees / filter files.

**Database** (`AppDbContext.cs`): EF Core + SQLite stored at `%LOCALAPPDATA%\WorkSpaceApp\workspace.db`. Schema is created via `EnsureCreatedAsync()` at startup (no EF migrations in use). Hard 1 GB cap enforced by `EnforceStorageLimit()` — call this before any sync/download.

**User identity** (`HardwareIdService.cs`): Hardware UUID = SHA-256(motherboard serial + CPU ProcessorId). This is the primary user key throughout — `Note.OwnerHardwareId`, `UserRole.HardwareId`, `HardwareRegistration.HardwareId` all reference it.

**Sync** (`SignalRService.cs`): Connects to a remote SignalR hub. Sends a heartbeat every 15 s. File download is triple-gated: caller role must be Owner/Editor, local DB must be < 1 GB, and owner PC must be online. The hub URL is passed into `ConnectAsync` — there is no hardcoded server URL in this client.

**Audit trail** (`ChangeLog` model): Every Create/Update/Delete on `Note` and `DynamicTable` must append an immutable `ChangeLog` row. Never delete or update `ChangeLog` rows.

**Key data shapes:**
- `Note.ContentMarkdown` stores HTML (despite the name) — used for WebView2/Quill rendering and stripped to plain text for QuestPDF export.
- `DynamicTable.SchemaJson` is a JSON array: `[{ "name": "...", "type": "Text|Number|Date|Boolean|Select" }]`. Row data lives in `DynamicRow.DataJson` as a JSON object keyed by column name.
- `FileTag` associates filesystem `.md` paths (not DB Note IDs) with `Tag` entities — used by the file tree sidebar.

**ViewModels** use CommunityToolkit.Mvvm source generators (`[ObservableProperty]`, `[RelayCommand]`). The generated backing fields are lowercase with underscore prefix (`_title` → `Title` property).

**WinUI 3 quirks to be aware of:**
- Always marshal back to the UI thread via `DispatcherQueue.TryEnqueue(...)` when updating bound properties from async/background code.
- Before clearing a `TreeView`-bound `ObservableCollection`, collapse all nodes first (`IsExpanded = false`) to prevent WinUI 3 layout crashes — see `FileTreeViewModel.SafeClearTree`.
- Window sizing uses Win32 interop: `WinRT.Interop.WindowNative.GetWindowHandle` + `AppWindow.Resize`.

## Key Files

| File | Purpose |
|---|---|
| `App.xaml.cs` | DI host construction, DB init, app entry point |
| `MainWindow.xaml.cs` | Navigation shell, theme toggle, storage/connection status bar |
| `Models.cs` | All EF Core entities (Note, Tag, DynamicTable, ChangeLog, UserRole, etc.) |
| `AppDbContext.cs` | EF DbContext, storage limit logic, DB file path |
| `SignalRService.cs` | Hub client, heartbeat, download gating |
| `HardwareIdService.cs` | WMI-based hardware UUID derivation |
| `NoteEditorViewModel.cs` | Notes CRUD, tag management, PDF export, file-mode load/save |
| `FileTreeViewModel.cs` | Filesystem `.md` tree, tag-per-file toggling |
| `RemindersViewModel.cs` | In-memory reminders (not yet persisted to DB) |
