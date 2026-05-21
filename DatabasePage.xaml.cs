using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using WorkSpaceApp.Features.Database.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace WorkSpaceApp.Features.Database.Views;

public sealed partial class DatabasePage : Page
{
    public DatabaseViewModel ViewModel { get; }

    public DatabasePage()
    {
        ViewModel = App.Services.GetRequiredService<DatabaseViewModel>();
        InitializeComponent();
        _ = ViewModel.LoadAsync();
    }

    private void Row_Clicked(object sender, ItemClickEventArgs e)
    {
        // Handle row click – open edit flyout in production
    }
}
