using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.EntityFrameworkCore;
using QuestPDF.Fluent;
using QuestPDF.Infrastructure;
using System.Collections.ObjectModel;
using WorkSpaceApp.Core.Models;
using WorkSpaceApp.Core.Services;
using WorkSpaceApp.Infrastructure.Data;
using Microsoft.Extensions.DependencyInjection;
using QuestPDF.Helpers;

namespace WorkSpaceApp.Features.Database.ViewModels;

/// <summary>Flat row display model for the DataGrid.</summary>
public class RowDisplayItem
{
    public Guid   Id                  { get; set; }
    public string Title               { get; set; } = string.Empty;
    public DateTime UpdatedAt         { get; set; }
    public EntryStatus Status         { get; set; }
    public string? AssignedUser       { get; set; }
    public bool   HasAttachment       { get; set; }
    public List<string> TagNames      { get; set; } = [];
    public string AssignedUserInitials =>
        string.IsNullOrEmpty(AssignedUser) ? "?" :
        string.Concat(AssignedUser.Split(' ').Select(p => p[0].ToString().ToUpper()));
}

public partial class DatabaseViewModel : ObservableObject
{
    private readonly AppDbContext _db;

    [ObservableProperty] private string _searchQuery   = string.Empty;
    [ObservableProperty] private string _selectedTagFilter = "All Tags";

    public ObservableCollection<RowDisplayItem> FilteredRows   { get; } = [];
    public ObservableCollection<string>         AvailableTags  { get; } = ["All Tags"];

    private List<RowDisplayItem> _allRows = [];

    public DatabaseViewModel(AppDbContext db) => _db = db;

    public async Task LoadAsync()
    {
        // Load DynamicRows from the first available table (demo)
        var rows = await _db.DynamicRows
            .Include(r => r.DynamicTable)
            .OrderByDescending(r => r.UpdatedAt)
            .ToListAsync();

        _allRows = rows.Select(r => new RowDisplayItem
        {
            Id           = r.Id,
            Title        = r.DataJson, // parse title from JSON in production
            UpdatedAt    = r.UpdatedAt,
            Status       = r.Status,
            AssignedUser = r.AssignedUser,
        }).ToList();

        ApplyFilter();
    }

    partial void OnSearchQueryChanged(string value)   => ApplyFilter();
    partial void OnSelectedTagFilterChanged(string v) => ApplyFilter();

    private void ApplyFilter()
    {
        var filtered = _allRows.AsEnumerable();

        if (!string.IsNullOrWhiteSpace(SearchQuery))
            filtered = filtered.Where(r =>
                r.Title.Contains(SearchQuery, StringComparison.OrdinalIgnoreCase));

        if (SelectedTagFilter != "All Tags")
            filtered = filtered.Where(r => r.TagNames.Contains(SelectedTagFilter));

        FilteredRows.Clear();
        foreach (var row in filtered)
            FilteredRows.Add(row);
    }

    [RelayCommand]
    public async Task AddRowAsync()
    {
        // Get or create a default table
        var table = await _db.DynamicTables.FirstOrDefaultAsync();
        if (table is null)
        {
            table = new DynamicTable
            {
                Name            = "Default",
                OwnerHardwareId = App.Services.GetRequiredService<IHardwareIdService>().GetHardwareId()
            };
            _db.DynamicTables.Add(table);
        }

        var newRow = new DynamicRow
        {
            DynamicTableId = table.Id,
            DataJson       = "{\"title\":\"New Entry\"}",
            Status         = EntryStatus.Pending
        };
        _db.DynamicRows.Add(newRow);

        // Business Rule: log every creation in the audit trail
        _db.ChangeLogs.Add(new ChangeLog
        {
            DynamicTableId = table.Id,
            ChangeType     = "Create",
            Description    = "New row added",
            ChangedBy      = "You"
        });

        await _db.SaveChangesAsync();
        await LoadAsync();
    }

    [RelayCommand]
    public async Task ExportPdfAsync()
    {
        QuestPDF.Settings.License = LicenseType.Community;

        string path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
            $"DatabaseExport_{DateTime.Now:yyyyMMdd_HHmm}.pdf");

        var rows = _allRows.ToList();

        await Task.Run(() =>
        {
            Document.Create(c => c.Page(page =>
            {
                page.Size(PageSizes.A4.Landscape());
                page.Margin(30, Unit.Point);
                page.DefaultTextStyle(t => t.FontFamily("Segoe UI").FontSize(10));
                page.Header().Text("Database Export")
                    .FontSize(16).Bold()
                    .FontColor(Colors.Blue.Medium);
                page.Content().Table(table =>
                {
                    table.ColumnsDefinition(c =>
                    {
                        c.RelativeColumn(3);
                        c.RelativeColumn(2);
                        c.RelativeColumn(2);
                        c.RelativeColumn(2);
                    });
                    table.Header(h =>
                    {
                        foreach (var col in new[] { "Title", "Date", "Status", "Assigned" })
                            h.Cell().Background(Colors.Grey.Lighten3)
                             .Padding(6).Text(col).Bold();
                    });
                    foreach (var row in rows)
                    {
                        table.Cell().Padding(6).Text(row.Title);
                        table.Cell().Padding(6).Text(row.UpdatedAt.ToString("MMM d, yyyy"));
                        table.Cell().Padding(6).Text(row.Status.ToString());
                        table.Cell().Padding(6).Text(row.AssignedUser ?? "-");
                    }
                });
            })).GeneratePdf(path);
        });
    }
}
