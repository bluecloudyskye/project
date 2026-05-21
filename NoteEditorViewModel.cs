// ============================================================
// Features/Notes/ViewModels/NoteEditorViewModel.cs
// MVVM ViewModel for the rich-text note editor page.
// Uses CommunityToolkit.Mvvm source generators.
// ============================================================
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.EntityFrameworkCore;
using QuestPDF.Fluent;
using QuestPDF.Infrastructure;
using WorkSpaceApp.Core.Models;
using WorkSpaceApp.Core.Services;
using WorkSpaceApp.Infrastructure.Data;
using System.Collections.ObjectModel;

namespace WorkSpaceApp.Features.Notes.ViewModels;

public partial class NoteEditorViewModel : ObservableObject
{
    private readonly AppDbContext       _db;
    private readonly IHardwareIdService _hwId;

    private Guid _currentNoteId = Guid.Empty;

    // ── Bindable properties ───────────────────────────────────

    [ObservableProperty]
    private string _noteTitle = "Untitled Note";

    [ObservableProperty]
    private string _contentMarkdown = string.Empty;

    [ObservableProperty]
    private string _newTagText = string.Empty;

    private string _currentFilePath = string.Empty;

    /// <summary>Tags attached to the current note.</summary>
    public ObservableCollection<Tag> Tags { get; } = [];

    /// <summary>Audit history displayed in the right panel.</summary>
    public ObservableCollection<ChangeLog> ChangeLogs { get; } = [];

    public NoteEditorViewModel(AppDbContext db, IHardwareIdService hwId)
    {
        _db   = db;
        _hwId = hwId;
    }

    public async Task LoadFromFileAsync(string filePath)
    {
        if (!File.Exists(filePath)) return;

        // Не перезагружаем тот же файл
        if (_currentFilePath == filePath) return;

        // Сохраняем текущий файл перед сменой
        if (!string.IsNullOrEmpty(_currentFilePath))
            await SaveToFileAsync();

        _currentFilePath = filePath;
        NoteTitle = Path.GetFileNameWithoutExtension(filePath);
        ContentMarkdown = await File.ReadAllTextAsync(filePath);
    }

    /// <summary>Сохраняет markdown обратно на диск.</summary>
    public async Task SaveToFileAsync()
    {
        if (string.IsNullOrEmpty(_currentFilePath)) return;
        await File.WriteAllTextAsync(_currentFilePath, ContentMarkdown);
    }

    // ─────────────────────────────────────────────────────────
    /// <summary>Load or create a note by ID.</summary>
    public async Task LoadNoteAsync(Guid? noteId = null)
    {
        Note? note;

        if (noteId.HasValue)
        {
            note = await _db.Notes
                .Include(n => n.NoteTags).ThenInclude(nt => nt.Tag)
                .Include(n => n.ChangeLogs)
                .FirstOrDefaultAsync(n => n.Id == noteId.Value);
        }
        else
        {
            // Create a new note in DB
            note = new Note
            {
                Id = Guid.NewGuid(),
                OwnerHardwareId = _hwId.GetHardwareId(),
                Title           = "Untitled Note"
            };

            _db.Notes.Add(note);

            await _db.SaveChangesAsync();

            // Business Rule: log the creation event
            _db.ChangeLogs.Add(new ChangeLog
            {
                NoteId     = note.Id,
                ChangeType = "Create",
                Description = "Note created",
                ChangedBy   = "You"

            });

            await _db.SaveChangesAsync();
        }

        if (note is null) return;

        _currentNoteId = note.Id;
        NoteTitle      = note.Title;
        ContentMarkdown = note.ContentMarkdown  ?? string.Empty;

        Tags.Clear();
        foreach (var nt in note.NoteTags)
            Tags.Add(nt.Tag);

        ChangeLogs.Clear();
        foreach (var cl in note.ChangeLogs.OrderByDescending(c => c.ChangedAt).Take(20))
            ChangeLogs.Add(cl);
    }

    // ─────────────────────────────────────────────────────────
    [RelayCommand]
    public async Task SaveAsync()
    {
        // Файловый режим
        if (!string.IsNullOrEmpty(_currentFilePath))
        {
            await SaveToFileAsync();
            return;
        }

        if (_currentNoteId == Guid.Empty) return;

        var note = await _db.Notes.FindAsync(_currentNoteId);
        if (note is null) return;

        string oldTitle = note.Title;
        note.Title = NoteTitle;
        note.ContentMarkdown = ContentMarkdown;
        note.UpdatedAt = DateTime.UtcNow;

        _db.ChangeLogs.Add(new ChangeLog
        {
            NoteId = _currentNoteId,
            ChangeType = "Update",
            Description = oldTitle != NoteTitle ? $"Title changed to '{NoteTitle}'" : "Content edited",
            ChangedBy = "You"
        });

        await _db.SaveChangesAsync();
        await RefreshChangeLogsAsync();
    }

    // ─────────────────────────────────────────────────────────
    [RelayCommand]
    public async Task AddTagAsync()
    {
        string name = NewTagText.Trim();
        if (string.IsNullOrEmpty(name)) return;
        if (Tags.Any(t => t.Name.Equals(name, StringComparison.OrdinalIgnoreCase))) return;

        // Reuse existing tag or create new
        var tag = await _db.Tags.FirstOrDefaultAsync(t => t.Name == name)
                  ?? new Tag { Name = name };

        if (tag.Id == Guid.Empty)
            _db.Tags.Add(tag);

        _db.NoteTags.Add(new NoteTag { NoteId = _currentNoteId, TagId = tag.Id });

        // Business Rule: log tag additions in audit trail
        _db.ChangeLogs.Add(new ChangeLog
        {
            NoteId      = _currentNoteId,
            ChangeType  = "TagAdded",
            Description = $"Tag '{name}' added",
            ChangedBy   = "You"
        });

        await _db.SaveChangesAsync();
        Tags.Add(tag);
        NewTagText = string.Empty;
        await RefreshChangeLogsAsync();
    }

    // ─────────────────────────────────────────────────────────
    [RelayCommand]
    public async Task RemoveTagAsync(Tag tag)
    {
        var noteTag = await _db.NoteTags
            .FirstOrDefaultAsync(nt => nt.NoteId == _currentNoteId && nt.TagId == tag.Id);

        if (noteTag is null) return;
        _db.NoteTags.Remove(noteTag);

        _db.ChangeLogs.Add(new ChangeLog
        {
            NoteId      = _currentNoteId,
            ChangeType  = "TagRemoved",
            Description = $"Tag '{tag.Name}' removed",
            ChangedBy   = "You"
        });

        await _db.SaveChangesAsync();
        Tags.Remove(tag);
        await RefreshChangeLogsAsync();
    }

    // ─────────────────────────────────────────────────────────
    /// <summary>
    /// Business Rule: Export the current note to PDF using QuestPDF.
    /// The HTML content is stripped to plain text for the PDF body;
    /// a production implementation would use a proper HTML→PDF renderer.
    /// </summary>
    [RelayCommand]
    public async Task ExportPdfAsync()
    {
        if (_currentNoteId == Guid.Empty) return;

        // QuestPDF Community license for open-source use
        QuestPDF.Settings.License = LicenseType.Community;

        string outputPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
            $"{NoteTitle}_{DateTime.Now:yyyyMMdd_HHmm}.pdf");

        string plainText = System.Text.RegularExpressions.Regex
            .Replace(ContentMarkdown, "<.*?>", string.Empty);

        await Task.Run(() =>
        {
            Document.Create(container =>
            {
                container.Page(page =>
                {
                    page.Size(QuestPDF.Helpers.PageSizes.A4);
                    page.Margin(40, QuestPDF.Infrastructure.Unit.Point);
                    page.DefaultTextStyle(t => t
                        .FontFamily("Segoe UI")
                        .FontSize(11));

                    page.Header().Text(NoteTitle)
                        .FontSize(20)
                        .FontFamily("Segoe UI Variable Display")
                        .Bold()
                        .FontColor(QuestPDF.Helpers.Colors.Blue.Medium);

                    page.Content()
                        .PaddingTop(16)
                        .Text(plainText);

                    page.Footer()
                        .AlignRight()
                        .Text(x =>
                        {
                            x.Span("Exported by WorkSpace · ");
                            x.CurrentPageNumber();
                        });
                });
            }).GeneratePdf(outputPath);
        });

        // TODO: open file in default PDF viewer or show a toast
    }

    // ─────────────────────────────────────────────────────────
    [RelayCommand]
    public void OpenShare()
    {
        // Navigates to CollaborationPage or opens a ContentDialog
        // Handled in code-behind via messaging
    }

    // ─────────────────────────────────────────────────────────
    private async Task RefreshChangeLogsAsync()
    {
        var logs = await _db.ChangeLogs
            .Where(cl => cl.NoteId == _currentNoteId)
            .OrderByDescending(cl => cl.ChangedAt)
            .Take(20)
            .ToListAsync();

        ChangeLogs.Clear();
        foreach (var cl in logs)
            ChangeLogs.Add(cl);
    }
}
