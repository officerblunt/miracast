using System;
using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Miracast.Avalonia.ViewModels;

public partial class MainWindowViewModel : ViewModelBase
{
    private const int MaximumStatusLines = 10;
    private readonly Queue<string> _statusLines = new();

    [ObservableProperty]
    private string _status = "Starting Miracast receiver…";

    [ObservableProperty]
    private string _statusHistory = "Starting Miracast receiver…";

    public void AppendStatus(string status)
    {
        Status = status;
        if (_statusLines.Count == 0 || !_statusLines.Last().Equals(status, StringComparison.Ordinal))
            _statusLines.Enqueue(status);
        while (_statusLines.Count > MaximumStatusLines)
            _statusLines.Dequeue();
        StatusHistory = string.Join(Environment.NewLine, _statusLines);
    }

}
