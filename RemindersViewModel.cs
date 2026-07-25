// ============================================================
// Features/Reminders/ViewModels/RemindersViewModel.cs
// Business Rule: Manages reminder items with filtering (All/Local/Synced)
// and completion tracking. Reminders can be local or synced from other users.
// Data Flow: 
//   1. User toggles IsCompleted → triggers property change notifications
//   2. UI updates Active/Completed lists via Filtered() query
//   3. Filter changes re-query the _all collection
// ============================================================
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.ObjectModel;

namespace WorkSpaceApp.Features.Reminders.ViewModels;

/// <summary>
/// Represents a single reminder item in the UI.
/// Data Flow: This object is bound to XAML DataTemplate properties.
/// Changes to IsCompleted trigger notifications that refresh parent lists.
/// </summary>
public partial class ReminderItem : ObservableObject
{
    public string  Id       { get; set; } = Guid.NewGuid().ToString();
    public bool    IsSynced { get; set; }
    public string? Source   { get; set; }
    public string  Priority { get; set; } = "medium"; // high | medium | low

    [ObservableProperty]
    private string _title = string.Empty;

    [ObservableProperty]
    private string _description = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FormattedTime), nameof(DueDate), nameof(DueTime))]
    private DateTime _time;

    [ObservableProperty]
    private bool _isCompleted;

    public string FormattedTime
    {
        get
        {
            var now = DateTime.Now;
            string t = Time.ToString("h:mm tt");
            if (Time.Date == now.Date)              return $"Today at {t}";
            if (Time.Date == now.Date.AddDays(1))   return $"Tomorrow at {t}";
            return Time.ToString("MMM d, h:mm tt");
        }
    }

    // Bridges for DatePicker / TimePicker (DateTimeOffset / TimeSpan bindings)
    public DateTimeOffset DueDate
    {
        get => new DateTimeOffset(Time.Year, Time.Month, Time.Day, 0, 0, 0, TimeSpan.Zero);
        set => Time = new DateTime(value.Year, value.Month, value.Day,
                                   Time.Hour, Time.Minute, Time.Second);
    }

    public TimeSpan DueTime
    {
        get => Time.TimeOfDay;
        set => Time = Time.Date + value;
    }

    public Action<ReminderItem>? OnCompletionChanged { get; set; }

    partial void OnIsCompletedChanged(bool value) => OnCompletionChanged?.Invoke(this);
}

/// <summary>
/// Manages the collection of reminders with filtering and CRUD operations.
/// Data Flow:
///   1. XAML binds to ActiveReminders/CompletedReminders via x:Bind
///   2. Filter changes trigger OnPropertyChanged for all dependent properties
///   3. Item completion changes propagate via OnCompletionChanged callback
/// </summary>
public partial class RemindersViewModel : ObservableObject
{
    // Internal storage for all reminders (both active and completed)
    private readonly ObservableCollection<ReminderItem> _all = [];

    /// <summary>
    /// Current filter mode: "all" | "local" | "synced"
    /// Data Flow: Bound to filter pill buttons in XAML header.
    /// Changing this triggers refresh of all computed properties.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ActiveReminders), nameof(CompletedReminders),
                               nameof(HasActive), nameof(HasCompleted),
                               nameof(ActiveCount), nameof(CompletedCount))]
    private string _filter = "all";

    /// <summary>
    /// Filtered view of non-completed reminders.
    /// Data Flow: ItemsControl in XAML binds to this for the active list.
    /// </summary>
    public IEnumerable<ReminderItem> ActiveReminders    => Filtered().Where(r => !r.IsCompleted);
    
    /// <summary>
    /// Filtered view of completed reminders.
    /// Data Flow: ItemsControl in XAML binds to this for the completed section.
    /// </summary>
    public IEnumerable<ReminderItem> CompletedReminders => Filtered().Where(r => r.IsCompleted);
    
    /// <summary>True if there are any active reminders to display.</summary>
    public bool HasActive    => ActiveReminders.Any();
    
    /// <summary>True if there are any completed reminders to display.</summary>
    public bool HasCompleted => CompletedReminders.Any();
    
    /// <summary>Count of active reminders for badge display.</summary>
    public int  ActiveCount    => ActiveReminders.Count();
    
    /// <summary>Count of completed reminders for badge display.</summary>
    public int  CompletedCount => CompletedReminders.Count();
    
    /// <summary>True when filter is set to "all" (used for button enable/disable).</summary>
    public bool IsFilterAll  => Filter == "all";

    public RemindersViewModel()
    {
        // Seed demo data for development/testing
        // Data Flow: These items populate the initial UI state on page load
        _all.Add(new ReminderItem { Title = "Review Marketing Strategy", Description = "Review and approve Q1 marketing strategy document", Time = DateTime.Now.AddHours(2),  Priority = "high", 
            OnCompletionChanged = _ => RefreshLists() });
        _all.Add(new ReminderItem { Title = "Team Meeting",              Description = "Weekly sync with development team",                  Time = DateTime.Now.AddHours(6),  Priority = "medium", IsSynced = true, Source = "Sarah Johnson", 
            OnCompletionChanged = _ => RefreshLists() });
        _all.Add(new ReminderItem { Title = "Client Call",               Description = "Discuss project requirements with client",           Time = DateTime.Now.AddDays(1),   Priority = "high",   IsSynced = true, Source = "Michael Chen", 
            OnCompletionChanged = _ => RefreshLists() });
        _all.Add(new ReminderItem { Title = "Update Documentation",      Description = "Update API documentation for new endpoints",         Time = DateTime.Now.AddDays(1).AddHours(6), Priority = "low", 
            OnCompletionChanged = _ => RefreshLists() });
        _all.Add(new ReminderItem { Title = "Code Review",               Description = "Review pull requests from team members",             Time = DateTime.Now.AddDays(-1),  Priority = "medium", IsSynced = true, Source = "Emily Davis", IsCompleted = true, 
            OnCompletionChanged = _ => RefreshLists() });
    }

    /// <summary>
    /// Applies the current filter to the _all collection.
    /// Data Flow: Called by all public list properties to get filtered results.
    /// </summary>
    private IEnumerable<ReminderItem> Filtered() => Filter switch
    {
        "local"  => _all.Where(r => !r.IsSynced),
        "synced" => _all.Where(r => r.IsSynced),
        _        => _all
    };

    /// <summary>
    /// Refreshes all list-dependent properties after a data change.
    /// Data Flow: Called when an item's IsCompleted property changes.
    /// </summary>
    private void RefreshLists()
    {
        OnPropertyChanged(nameof(ActiveReminders));
        OnPropertyChanged(nameof(CompletedReminders));
        OnPropertyChanged(nameof(HasActive));
        OnPropertyChanged(nameof(HasCompleted));
        OnPropertyChanged(nameof(ActiveCount));
        OnPropertyChanged(nameof(CompletedCount));
    }

    /// <summary>
    /// Changes the filter mode (All/Local/Synced).
    /// Data Flow: Bound to filter pill buttons via CommandParameter.
    /// </summary>
    [RelayCommand]
    public void SetFilter(string filter)
    {
        Filter = filter;
        // Explicit notification ensures UI updates even if filter value is same
        RefreshLists();
    }

    /// <summary>
    /// Removes a reminder from the collection.
    /// Data Flow: Bound to delete button in each reminder card.
    /// </summary>
    [RelayCommand]
    public void DeleteReminder(ReminderItem item)
    {
        _all.Remove(item);
        RefreshLists();
    }

    /// <summary>
    /// Creates a new blank reminder at the top of the list.
    /// Data Flow: Bound to "New Reminder" button in header.
    /// </summary>
    [RelayCommand]
    public void AddReminder()
    {
        var newItem = new ReminderItem
        {
            Title       = "New Reminder",
            Description = "Add a description...",
            Time        = DateTime.Now.AddHours(1),
            Priority    = "medium",
            OnCompletionChanged = _ => RefreshLists()
        };
        _all.Insert(0, newItem);
        RefreshLists();
    }
}
