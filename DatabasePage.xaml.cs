using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WorkSpaceApp.Features.Database.ViewModels;

namespace WorkSpaceApp.Features.Database.Views;

public sealed partial class DatabasePage : Page
{
    public DatabaseViewModel ViewModel { get; }

    private bool _syncingSelection;

    public DatabasePage()
    {
        ViewModel = App.Services.GetRequiredService<DatabaseViewModel>();
        InitializeComponent();
        ViewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ViewModel.SelectedTable))
            {
                SyncListViewSelection();
                _ = ViewModel.LoadTableDataAsync();
            }
        };
        _ = ViewModel.LoadTablesAsync();
    }

    private void SyncListViewSelection()
    {
        var selectedId = ViewModel.SelectedTable?.Id;
        if (selectedId is null) return;
        for (int i = 0; i < ViewModel.Tables.Count; i++)
        {
            if (ViewModel.Tables[i].Id != selectedId) continue;
            _syncingSelection = true;
            TablesList.SelectedIndex = i;
            _syncingSelection = false;
            return;
        }
    }

    private void TablesList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingSelection) return;
        if (e.AddedItems.FirstOrDefault() is DbTableInfo table)
            ViewModel.SelectedTable = table;
    }

    // ─────────────────────────────────────────────────────────
    // New table
    // ─────────────────────────────────────────────────────────
    private async void NewTable_Click(object sender, RoutedEventArgs e)
    {
        var nameBox = new TextBox { PlaceholderText = "Table name", Margin = new Thickness(0, 0, 0, 8) };
        var colsBox = new TextBox { PlaceholderText = "Columns (через запятую)" };

        var panel = new StackPanel { Spacing = 4 };
        panel.Children.Add(new TextBlock { Text = "Table name", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        panel.Children.Add(nameBox);
        panel.Children.Add(new TextBlock { Text = "Columns", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Margin = new Thickness(0, 8, 0, 0) });
        panel.Children.Add(colsBox);

        var dialog = new ContentDialog
        {
            Title             = "Create New Table",
            Content           = panel,
            PrimaryButtonText = "Create",
            CloseButtonText   = "Cancel",
            XamlRoot          = XamlRoot
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        var name = nameBox.Text.Trim();
        if (string.IsNullOrEmpty(name)) return;

        var cols = (colsBox.Text ?? "Value")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (cols.Length == 0) cols = ["Value"];

        await ViewModel.CreateTableAsync(name, cols);
    }

    // ─────────────────────────────────────────────────────────
    // New row — builds TextBox or CheckBox per column type
    // ─────────────────────────────────────────────────────────
    private async void NewRow_Click(object sender, RoutedEventArgs e)
    {
        var cols  = await ViewModel.GetCurrentColumnsAsync();
        var panel = new StackPanel { Spacing = 4 };
        var inputs = new List<(string Name, string Type, object Control)>();

        foreach (var col in cols)
        {
            panel.Children.Add(new TextBlock
            {
                Text       = col.Name,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Margin     = new Thickness(0, 6, 0, 0)
            });
            if (col.Type == "Boolean")
            {
                var cb = new CheckBox { Content = "Checked" };
                panel.Children.Add(cb);
                inputs.Add((col.Name, "Boolean", cb));
            }
            else
            {
                var box = new TextBox { PlaceholderText = $"Enter {col.Name}..." };
                panel.Children.Add(box);
                inputs.Add((col.Name, "Text", box));
            }
        }

        var dialog = new ContentDialog
        {
            Title             = "Add Row",
            Content           = new ScrollViewer { Content = panel, MaxHeight = 400 },
            PrimaryButtonText = "Add",
            CloseButtonText   = "Cancel",
            XamlRoot          = XamlRoot
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        var values = inputs.ToDictionary(
            i => i.Name,
            i => i.Type == "Boolean"
                ? (((CheckBox)i.Control).IsChecked == true ? "true" : "false")
                : ((TextBox)i.Control).Text.Trim());

        await ViewModel.AddRowAsync(values);
    }

    // ─────────────────────────────────────────────────────────
    // Delete table
    // ─────────────────────────────────────────────────────────
    private async void DeleteTable_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new ContentDialog
        {
            Title             = "Delete Table",
            Content           = $"Delete '{ViewModel.SelectedTableName}' and all its rows? This cannot be undone.",
            PrimaryButtonText = "Delete",
            CloseButtonText   = "Cancel",
            XamlRoot          = XamlRoot
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
            await ViewModel.DeleteTableAsync();
    }

    // ─────────────────────────────────────────────────────────
    // Row click → edit dialog (fires for text cells; CheckBox clicks stop propagation)
    // ─────────────────────────────────────────────────────────
    private async void RowsList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (!ViewModel.CanAddRow) return;
        if (e.ClickedItem is DbRowItem row)
            await ShowEditRowDialogAsync(row);
    }

    // ─────────────────────────────────────────────────────────
    // Edit / Delete from 3-dot menu
    // ─────────────────────────────────────────────────────────
    private async void EditRow_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuFlyoutItem { DataContext: DbRowItem row })
            await ShowEditRowDialogAsync(row);
    }

    private async void DeleteRow_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuFlyoutItem { DataContext: DbRowItem row })
            await ViewModel.DeleteRowAsync(row);
    }

    // ─────────────────────────────────────────────────────────
    // Shared edit dialog — shows TextBox or CheckBox per column type
    // ─────────────────────────────────────────────────────────
    private async Task ShowEditRowDialogAsync(DbRowItem row)
    {
        if (!ViewModel.CanAddRow) return;

        var cols    = await ViewModel.GetCurrentColumnsAsync();
        var current = new[] { row.Col0, row.Col1, row.Col2, row.Col3 };

        var panel  = new StackPanel { Spacing = 4 };
        var inputs = new List<(string Name, string Type, object Control)>();

        for (int i = 0; i < cols.Count; i++)
        {
            panel.Children.Add(new TextBlock
            {
                Text       = cols[i].Name,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Margin     = new Thickness(0, 6, 0, 0)
            });

            if (cols[i].Type == "Boolean")
            {
                bool cur = i < current.Length && current[i].Equals("true", StringComparison.OrdinalIgnoreCase);
                var cb = new CheckBox { Content = "Checked", IsChecked = cur };
                panel.Children.Add(cb);
                inputs.Add((cols[i].Name, "Boolean", cb));
            }
            else
            {
                var box = new TextBox { Text = i < current.Length ? current[i] : "" };
                panel.Children.Add(box);
                inputs.Add((cols[i].Name, "Text", box));
            }
        }

        var dialog = new ContentDialog
        {
            Title             = "Edit Row",
            Content           = new ScrollViewer { Content = panel, MaxHeight = 400 },
            PrimaryButtonText = "Save",
            CloseButtonText   = "Cancel",
            XamlRoot          = XamlRoot
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        var values = inputs.ToDictionary(
            i => i.Name,
            i => i.Type == "Boolean"
                ? (((CheckBox)i.Control).IsChecked == true ? "true" : "false")
                : ((TextBox)i.Control).Text.Trim());

        await ViewModel.EditRowAsync(row, values);
    }

    // ─────────────────────────────────────────────────────────
    // Checkbox cell toggle — does NOT trigger RowsList_ItemClick
    // ─────────────────────────────────────────────────────────
    private async void CellCheckbox_Click(object sender, RoutedEventArgs e)
    {
        if (!ViewModel.CanAddRow) return;
        if (sender is CheckBox { DataContext: DbRowItem row, Tag: string tagStr } cb
            && int.TryParse(tagStr, out int colIndex))
        {
            await ViewModel.ToggleCheckboxAsync(row.RowId, colIndex, cb.IsChecked == true);
        }
    }

    // ─────────────────────────────────────────────────────────
    // Column header — left-click renames, right-click shows context menu
    // ─────────────────────────────────────────────────────────
    private async void ColumnHeader_Click(object sender, RoutedEventArgs e)
    {
        if (!ViewModel.CanDeleteTable) return;
        if (sender is FrameworkElement { Tag: string tagStr } && int.TryParse(tagStr, out int colIndex))
            await RenameColumnDialogAsync(colIndex);
    }

    private async void RenameColumn_MenuClick(object sender, RoutedEventArgs e)
    {
        if (!ViewModel.CanDeleteTable) return;
        if (sender is FrameworkElement { Tag: string tagStr } && int.TryParse(tagStr, out int colIndex))
            await RenameColumnDialogAsync(colIndex);
    }

    private async Task RenameColumnDialogAsync(int colIndex)
    {
        var colNames = await ViewModel.GetCurrentColumnNamesAsync();
        if (colIndex >= colNames.Count) return;

        var nameBox = new TextBox { Header = "Column name", Text = colNames[colIndex] };
        var dialog  = new ContentDialog
        {
            Title             = "Rename Column",
            Content           = nameBox,
            PrimaryButtonText = "Rename",
            CloseButtonText   = "Cancel",
            XamlRoot          = XamlRoot
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        var newName = nameBox.Text.Trim();
        if (!string.IsNullOrEmpty(newName) && newName != colNames[colIndex])
            await ViewModel.RenameColumnAsync(colIndex, newName);
    }

    // ─────────────────────────────────────────────────────────
    // Make Checkbox Column (right-click context menu)
    // ─────────────────────────────────────────────────────────
    private async void MakeCheckboxColumn_Click(object sender, RoutedEventArgs e)
    {
        if (!ViewModel.CanAddRow) return;
        if (sender is FrameworkElement { Tag: string tagStr } && int.TryParse(tagStr, out int colIndex))
            await ViewModel.MakeColumnCheckboxAsync(colIndex);
    }
}
