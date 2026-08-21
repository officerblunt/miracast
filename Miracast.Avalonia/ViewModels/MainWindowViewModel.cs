using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using Miracast.Receiver;

namespace Miracast.Avalonia.ViewModels;

public partial class MainWindowViewModel : ViewModelBase
{
    [ObservableProperty]
    private string _status = "Starting Miracast receiver…";

    [ObservableProperty]
    private bool _hasAvailableSources;

    public ObservableCollection<MiracastSourceInfo> AvailableSources { get; } = [];

    public void UpdateSource(MiracastSourceInfo source, bool isAvailable)
    {
        var existing = AvailableSources.FirstOrDefault(candidate => candidate.Id == source.Id);
        if (existing is not null)
            AvailableSources.Remove(existing);
        if (isAvailable)
            AvailableSources.Add(source);
        HasAvailableSources = AvailableSources.Count > 0;
    }
}
