using Microsoft.UI.Xaml.Controls;
using Microsoft.Extensions.DependencyInjection;
using WorkSpaceApp.Features.Sync.ViewModels;

namespace WorkSpaceApp.Features.Sync.Views;

public sealed partial class CollaborationPage : Page
{
    public CollaborationViewModel ViewModel { get; }

    public CollaborationPage()
    {
        ViewModel = App.Services.GetRequiredService<CollaborationViewModel>();
        InitializeComponent();
    }
}
