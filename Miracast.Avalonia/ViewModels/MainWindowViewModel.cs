using CommunityToolkit.Mvvm.ComponentModel;

namespace Miracast.Avalonia.ViewModels;

public partial class MainWindowViewModel : ViewModelBase
{
    [ObservableProperty]
    private string _status = "Starting Miracast receiver…";
}
