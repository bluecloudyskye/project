using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.EntityFrameworkCore;
using Microsoft.UI.Xaml;
using System.Collections.ObjectModel;
using System.Text.Json;
using WorkSpaceApp.Core.Models;
using WorkSpaceApp.Core.Services;
using WorkSpaceApp.Features.Reminders.ViewModels;
using WorkSpaceApp.Infrastructure.Data;

namespace WorkSpaceApp.Features.Database.ViewModels;

public class DbTableInfo
{
    public string Id        { get; set; } = string.Empty;
    public string Name      { get; set; } = string.Empty;
    public string Glyph     { get; set; } = "";
    public bool   IsBuiltIn { get; set; }
    public Guid?  TableGuid { get; set; }
}

public class DbRowItem
{
    public string RowId { get; set; } = string.Empty;
    public string Col0  { get; set; } = string.Empty;
    public string Col1  { get; set; } = string.Empty;
    public string Col2  { get; set; } = string.Empty;
    public string Col3  { get; set; } = string.Empty;

    // Column type metadata populated from the table's SchemaJson
    public string ColType0 { get; set; } = "Text";
    public string ColType1 { get; set; } = "Text";
    public string ColType2 { get; set; } = "Text";
    public string ColType3 { get; set; } = "Text";

    // Whether the column actually exists in the schema (false = hide the cell)
    public bool HasCol1 { get; set; } = true;
    public bool HasCol2 { get; set; } = true;
    public bool HasCol3 { get; set; } = true;

    // Visibility helpers: gate on both HasColN and type so unused columns disappear
    public bool IsCheckCol0    => ColType0 == "Boolean";
    public bool IsCheckCol1    => HasCol1 && ColType1 == "Boolean";
    public bool IsCheckCol2    => HasCol2 && ColType2 == "Boolean";
    public bool IsCheckCol3    => HasCol3 && ColType3 == "Boolean";
    public bool IsNotCheckCol0 => ColType0 != "Boolean";
    public bool IsNotCheckCol1 => HasCol1 && ColType1 != "Boolean";
    public bool IsNotCheckCol2 => HasCol2 && ColType2 != "Boolean";
    public bool IsNotCheckCol3 => HasCol3 && ColType3 != "Boolean";

    // Parsed boolean values for CheckBox.IsChecked binding
    public bool CheckVal0 => Col0.Equals("true", StringComparison.OrdinalIgnoreCase);
    public bool CheckVal1 => Col1.Equals("true", StringComparison.OrdinalIgnoreCase);
    public bool CheckVal2 => Col2.Equals("true", StringComparison.OrdinalIgnoreCase);
    public bool CheckVal3 => Col3.Equals("true", StringComparison.OrdinalIgnoreCase);
}

public class ColumnDef
{
    public string Name { get; set; } = string.Empty;
    public string Type { get; set; } = "Text";
}

public partial class DatabaseViewModel : ObservableObject
{
    private readonly AppDbContext        _db;
    private readonly IHardwareIdService  _hwId;
    private readonly IUserProfileService _profile;
    private readonly RemindersViewModel  _remindersVm;

    [ObservableProperty]
    [NotifyPropertyChangedFor(
        nameof(SelectedTableName),
        nameof(CanAddRow),
        nameof(CanDeleteTable),
        nameof(AddRowVisibility),
        nameof(DeleteTableVisibility))]
    private DbTableInfo? _selectedTable;

    public ObservableCollection<DbTableInfo> Tables { get; } = [];
    public ObservableCollection<DbRowItem>   Rows   { get; } = [];

    public string     SelectedTableName     => SelectedTable?.Name ?? "Select a table";
    public bool       CanAddRow             => SelectedTable?.IsBuiltIn == false;
    public bool       CanDeleteTable        => SelectedTable?.IsBuiltIn == false;
    public Visibility AddRowVisibility      => CanAddRow      ? Visibility.Visible : Visibility.Collapsed;
    public Visibility DeleteTableVisibility => CanDeleteTable ? Visibility.Visible : Visibility.Collapsed;

    [ObservableProperty] private string _header0 = "COL 1";
    [ObservableProperty] private string _header1 = "COL 2";
    [ObservableProperty] private string _header2 = "COL 3";
    [ObservableProperty] private string _header3 = "COL 4";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ColHeader1Visible), nameof(ColHeader2Visible), nameof(ColHeader3Visible))]
    private int _columnCount = 4;

    public Visibility ColHeader1Visible => ColumnCount > 1 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility ColHeader2Visible => ColumnCount > 2 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility ColHeader3Visible => ColumnCount > 3 ? Visibility.Visible : Visibility.Collapsed;

    public DatabaseViewModel(AppDbContext db, IHardwareIdService hwId, IUserProfileService profile, RemindersViewModel remindersVm)
    {
        _db          = db;
        _hwId        = hwId;
        _profile     = profile;
        _remindersVm = remindersVm;
    }

    public async Task LoadTablesAsync()
    {
        var prevId = SelectedTable?.Id;
        Tables.Clear();

        Tables.Add(new DbTableInfo { Id = "builtin:notes",     Name = "Notes",     Glyph = "", IsBuiltIn = true });
        Tables.Add(new DbTableInfo { Id = "builtin:reminders", Name = "Reminders", Glyph = "", IsBuiltIn = true });
        Tables.Add(new DbTableInfo { Id = "builtin:filetags",  Name = "File Tags", Glyph = "", IsBuiltIn = true });

        var custom = await _db.DynamicTables.OrderBy(t => t.Name).ToListAsync();
        foreach (var t in custom)
            Tables.Add(new DbTableInfo { Id = t.Id.ToString(), Name = t.Name, Glyph = "", IsBuiltIn = false, TableGuid = t.Id });

        if (prevId is not null)
        {
            var prev = Tables.FirstOrDefault(t => t.Id == prevId);
            if (prev is not null) { SelectedTable = prev; return; }
        }
        if (Tables.Count > 0) SelectedTable = Tables[0];
    }

    public async Task LoadTableDataAsync()
    {
        if (SelectedTable is null) { Rows.Clear(); return; }
        switch (SelectedTable.Id)
        {
            case "builtin:notes":     ColumnCount = 4; await LoadNotesAsync();    break;
            case "builtin:reminders": ColumnCount = 4; LoadReminders();           break;
            case "builtin:filetags":  ColumnCount = 4; await LoadFileTagsAsync(); break;
            default:
                if (SelectedTable.TableGuid.HasValue)
                    await LoadCustomTableAsync(SelectedTable.TableGuid.Value);
                break;
        }
    }

    private async Task LoadNotesAsync()
    {
        Header0 = "TITLE"; Header1 = "UPDATED"; Header2 = ""; Header3 = "";
        var notes = await _db.Notes.OrderByDescending(n => n.UpdatedAt).ToListAsync();
        Rows.Clear();
        foreach (var n in notes)
            Rows.Add(new DbRowItem { RowId = n.Id.ToString(), Col0 = n.Title, Col1 = n.UpdatedAt.ToString("MMM d, yyyy") });
    }

    private void LoadReminders()
    {
        Header0 = "TITLE"; Header1 = "TIME"; Header2 = "PRIORITY"; Header3 = "STATUS";
        var all = _remindersVm.ActiveReminders.Concat(_remindersVm.CompletedReminders).ToList();
        Rows.Clear();
        foreach (var r in all)
            Rows.Add(new DbRowItem { RowId = r.Id, Col0 = r.Title, Col1 = r.FormattedTime, Col2 = r.Priority, Col3 = r.IsCompleted ? "Done" : "Active" });
    }

    private async Task LoadFileTagsAsync()
    {
        Header0 = "FILE PATH"; Header1 = "TAG"; Header2 = "COLOR"; Header3 = "";
        var entries = await _db.FileTags.Include(ft => ft.Tag).OrderBy(ft => ft.FilePath).ToListAsync();
        Rows.Clear();
        foreach (var ft in entries)
            Rows.Add(new DbRowItem { RowId = ft.Id.ToString(), Col0 = ft.FilePath, Col1 = ft.Tag?.Name ?? "", Col2 = ft.Tag?.Color ?? "" });
    }

    private async Task LoadCustomTableAsync(Guid tableId)
    {
        var table = await _db.DynamicTables.FirstOrDefaultAsync(t => t.Id == tableId);
        if (table is null) { Rows.Clear(); return; }

        List<ColumnDef> cols = [];
        try { cols = JsonSerializer.Deserialize<List<ColumnDef>>(table.SchemaJson) ?? []; } catch { }

        ColumnCount = cols.Count;
        Header0 = cols.Count > 0 ? cols[0].Name.ToUpper() : "COL 1";
        Header1 = cols.Count > 1 ? cols[1].Name.ToUpper() : "COL 2";
        Header2 = cols.Count > 2 ? cols[2].Name.ToUpper() : "COL 3";
        Header3 = cols.Count > 3 ? cols[3].Name.ToUpper() : "COL 4";

        var dbRows = await _db.DynamicRows
            .Where(r => r.DynamicTableId == tableId)
            .OrderByDescending(r => r.UpdatedAt)
            .ToListAsync();

        Rows.Clear();
        foreach (var row in dbRows)
        {
            var data = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                var doc = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(row.DataJson);
                if (doc is not null)
                    foreach (var kv in doc)
                        data[kv.Key] = kv.Value.ValueKind == JsonValueKind.String
                            ? kv.Value.GetString() ?? ""
                            : kv.Value.ToString();
            }
            catch { }

            Rows.Add(new DbRowItem
            {
                RowId    = row.Id.ToString(),
                Col0     = cols.Count > 0 ? data.GetValueOrDefault(cols[0].Name, "") : "",
                Col1     = cols.Count > 1 ? data.GetValueOrDefault(cols[1].Name, "") : "",
                Col2     = cols.Count > 2 ? data.GetValueOrDefault(cols[2].Name, "") : "",
                Col3     = cols.Count > 3 ? data.GetValueOrDefault(cols[3].Name, "") : "",
                ColType0 = cols.Count > 0 ? cols[0].Type : "Text",
                ColType1 = cols.Count > 1 ? cols[1].Type : "Text",
                ColType2 = cols.Count > 2 ? cols[2].Type : "Text",
                ColType3 = cols.Count > 3 ? cols[3].Type : "Text",
                HasCol1  = cols.Count > 1,
                HasCol2  = cols.Count > 2,
                HasCol3  = cols.Count > 3,
            });
        }
    }

    public async Task CreateTableAsync(string name, string[] columnNames)
    {
        var cols  = columnNames.Select(c => new ColumnDef { Name = c }).ToList();
        var table = new DynamicTable
        {
            Name            = name,
            SchemaJson      = JsonSerializer.Serialize(cols),
            OwnerHardwareId = _hwId.GetHardwareId()
        };
        _db.DynamicTables.Add(table);
        _db.ChangeLogs.Add(new ChangeLog { DynamicTableId = table.Id, ChangeType = "Create", Description = $"Table '{name}' created", ChangedBy = _profile.DisplayName });
        await _db.SaveChangesAsync();

        await LoadTablesAsync();
        var created = Tables.FirstOrDefault(t => t.TableGuid == table.Id);
        if (created is not null) SelectedTable = created;
    }

    public async Task AddRowAsync(Dictionary<string, string> values)
    {
        if (SelectedTable?.TableGuid is null) return;
        var row = new DynamicRow
        {
            DynamicTableId = SelectedTable.TableGuid.Value,
            DataJson       = JsonSerializer.Serialize(values)
        };
        _db.DynamicRows.Add(row);
        _db.ChangeLogs.Add(new ChangeLog { DynamicTableId = SelectedTable.TableGuid.Value, ChangeType = "Create", Description = "Row added", ChangedBy = _profile.DisplayName });
        await _db.SaveChangesAsync();
        await LoadTableDataAsync();
    }

    [RelayCommand]
    public async Task DeleteRowAsync(DbRowItem row)
    {
        if (SelectedTable is null) return;

        if (SelectedTable.Id == "builtin:notes" && Guid.TryParse(row.RowId, out var noteId))
        {
            var note = await _db.Notes.FindAsync(noteId);
            if (note is not null) _db.Notes.Remove(note);
            await _db.SaveChangesAsync();
        }
        else if (SelectedTable.Id == "builtin:filetags" && Guid.TryParse(row.RowId, out var ftId))
        {
            var ft = await _db.FileTags.FindAsync(ftId);
            if (ft is not null) _db.FileTags.Remove(ft);
            await _db.SaveChangesAsync();
        }
        else if (SelectedTable.TableGuid.HasValue && Guid.TryParse(row.RowId, out var rowId))
        {
            var dbRow = await _db.DynamicRows.FindAsync(rowId);
            if (dbRow is not null) _db.DynamicRows.Remove(dbRow);
            _db.ChangeLogs.Add(new ChangeLog { DynamicTableId = SelectedTable.TableGuid.Value, ChangeType = "Delete", Description = "Row deleted", ChangedBy = _profile.DisplayName });
            await _db.SaveChangesAsync();
        }

        await LoadTableDataAsync();
    }

    [RelayCommand]
    public async Task DeleteTableAsync()
    {
        if (SelectedTable?.TableGuid is null) return;
        var table = await _db.DynamicTables.FindAsync(SelectedTable.TableGuid.Value);
        if (table is not null) _db.DynamicTables.Remove(table);
        await _db.SaveChangesAsync();
        await LoadTablesAsync();
    }

    public async Task<List<string>> GetCurrentColumnNamesAsync()
    {
        if (SelectedTable?.TableGuid is null) return ["Value"];
        var table = await _db.DynamicTables.FirstOrDefaultAsync(t => t.Id == SelectedTable.TableGuid.Value);
        if (table is null) return ["Value"];
        try
        {
            var cols = JsonSerializer.Deserialize<List<ColumnDef>>(table.SchemaJson);
            return cols?.Select(c => c.Name).ToList() ?? ["Value"];
        }
        catch { return ["Value"]; }
    }

    public async Task EditRowAsync(DbRowItem row, Dictionary<string, string> newValues)
    {
        if (SelectedTable?.TableGuid is null) return;
        if (!Guid.TryParse(row.RowId, out var rowId)) return;

        var dbRow = await _db.DynamicRows.FindAsync(rowId);
        if (dbRow is null) return;

        dbRow.DataJson  = JsonSerializer.Serialize(newValues);
        dbRow.UpdatedAt = DateTime.UtcNow;
        _db.ChangeLogs.Add(new ChangeLog { DynamicTableId = SelectedTable.TableGuid.Value, ChangeType = "Update", Description = "Row edited", ChangedBy = _profile.DisplayName });
        await _db.SaveChangesAsync();
        await LoadTableDataAsync();
    }

    public async Task RenameColumnAsync(int colIndex, string newName)
    {
        if (SelectedTable?.TableGuid is null) return;

        var table = await _db.DynamicTables.FirstOrDefaultAsync(t => t.Id == SelectedTable.TableGuid.Value);
        if (table is null) return;

        List<ColumnDef> cols;
        try { cols = JsonSerializer.Deserialize<List<ColumnDef>>(table.SchemaJson) ?? []; }
        catch { return; }

        if (colIndex >= cols.Count) return;
        var oldName = cols[colIndex].Name;
        cols[colIndex] = new ColumnDef { Name = newName, Type = cols[colIndex].Type };
        table.SchemaJson = JsonSerializer.Serialize(cols);

        // Migrate existing row data to the new key name
        var rows = await _db.DynamicRows
            .Where(r => r.DynamicTableId == SelectedTable.TableGuid.Value)
            .ToListAsync();
        foreach (var r in rows)
        {
            try
            {
                var doc = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(r.DataJson);
                if (doc is null || !doc.ContainsKey(oldName)) continue;
                var updated = new Dictionary<string, string>();
                foreach (var kv in doc)
                    updated[kv.Key == oldName ? newName : kv.Key] =
                        kv.Value.ValueKind == JsonValueKind.String
                            ? kv.Value.GetString() ?? ""
                            : kv.Value.ToString();
                r.DataJson  = JsonSerializer.Serialize(updated);
                r.UpdatedAt = DateTime.UtcNow;
            }
            catch { }
        }

        _db.ChangeLogs.Add(new ChangeLog
        {
            DynamicTableId = SelectedTable.TableGuid.Value,
            ChangeType     = "Update",
            Description    = $"Column renamed: {oldName} → {newName}",
            ChangedBy      = _profile.DisplayName
        });
        await _db.SaveChangesAsync();
        await LoadTableDataAsync();
    }

    /// <summary>Returns full column definitions (name + type) for the selected custom table.</summary>
    public async Task<List<ColumnDef>> GetCurrentColumnsAsync()
    {
        if (SelectedTable?.TableGuid is null) return [new ColumnDef { Name = "Value" }];
        var table = await _db.DynamicTables.FirstOrDefaultAsync(t => t.Id == SelectedTable.TableGuid.Value);
        if (table is null) return [new ColumnDef { Name = "Value" }];
        try { return JsonSerializer.Deserialize<List<ColumnDef>>(table.SchemaJson) ?? [new ColumnDef { Name = "Value" }]; }
        catch { return [new ColumnDef { Name = "Value" }]; }
    }

    /// <summary>Converts a column's type to Boolean (Checkbox) and normalises existing row values.</summary>
    public async Task MakeColumnCheckboxAsync(int colIndex)
    {
        if (SelectedTable?.TableGuid is null) return;
        var table = await _db.DynamicTables.FirstOrDefaultAsync(t => t.Id == SelectedTable.TableGuid.Value);
        if (table is null) return;

        List<ColumnDef> cols;
        try { cols = JsonSerializer.Deserialize<List<ColumnDef>>(table.SchemaJson) ?? []; } catch { return; }
        if (colIndex >= cols.Count) return;

        var colName = cols[colIndex].Name;
        cols[colIndex] = new ColumnDef { Name = colName, Type = "Boolean" };
        table.SchemaJson = JsonSerializer.Serialize(cols);

        var rows = await _db.DynamicRows
            .Where(r => r.DynamicTableId == SelectedTable.TableGuid.Value)
            .ToListAsync();
        foreach (var row in rows)
        {
            try
            {
                var doc = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(row.DataJson);
                if (doc is null) continue;
                var data = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var kv in doc)
                    data[kv.Key] = kv.Value.ValueKind == JsonValueKind.String ? kv.Value.GetString() ?? "" : kv.Value.ToString();
                bool cur = data.TryGetValue(colName, out var v) && v.Equals("true", StringComparison.OrdinalIgnoreCase);
                data[colName] = cur ? "true" : "false";
                row.DataJson = JsonSerializer.Serialize(data);
            }
            catch { }
        }

        _db.ChangeLogs.Add(new ChangeLog
        {
            DynamicTableId = SelectedTable.TableGuid.Value,
            ChangeType     = "Update",
            Description    = $"Column '{colName}' converted to Checkbox",
            ChangedBy      = _profile.DisplayName
        });
        await _db.SaveChangesAsync();
        await LoadTableDataAsync();
    }

    /// <summary>Flips a single Boolean cell value and reloads the table.</summary>
    public async Task ToggleCheckboxAsync(string rowId, int colIndex, bool value)
    {
        if (!Guid.TryParse(rowId, out var id) || SelectedTable?.TableGuid is null) return;
        var dbRow = await _db.DynamicRows.FindAsync(id);
        if (dbRow is null) return;

        var cols = await GetCurrentColumnsAsync();
        if (colIndex >= cols.Count) return;
        var colName = cols[colIndex].Name;

        var data = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var doc = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(dbRow.DataJson);
            if (doc is not null)
                foreach (var kv in doc)
                    data[kv.Key] = kv.Value.ValueKind == JsonValueKind.String ? kv.Value.GetString() ?? "" : kv.Value.ToString();
        }
        catch { }

        data[colName]  = value ? "true" : "false";
        dbRow.DataJson = JsonSerializer.Serialize(data);
        dbRow.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        await LoadTableDataAsync();
    }
}
