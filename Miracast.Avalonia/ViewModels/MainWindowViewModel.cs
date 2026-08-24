using System;
using System.Collections.ObjectModel;
using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using Miracast.Receiver;

namespace Miracast.Avalonia.ViewModels;

public partial class MainWindowViewModel : ViewModelBase
{
    private const int MaximumStatusLines = 10;
    private readonly Queue<string> _statusLines = new();

    [ObservableProperty]
    private string _status = "Starting Miracast receiver…";

    [ObservableProperty]
    private string _statusHistory = "Starting Miracast receiver…";

    [ObservableProperty]
    private bool _hasAvailableSources;

    public ObservableCollection<MiracastSourceInfo> AvailableSources { get; } = [];

    public void AppendStatus(string status)
    {
        Status = status;
        if (_statusLines.Count == 0 || !_statusLines.Last().Equals(status, StringComparison.Ordinal))
            _statusLines.Enqueue(status);
        while (_statusLines.Count > MaximumStatusLines)
            _statusLines.Dequeue();
        StatusHistory = string.Join(Environment.NewLine, _statusLines);
    }

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
