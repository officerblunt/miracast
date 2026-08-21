using Avalonia;
using Microsoft.Extensions.DependencyInjection;
using Miracast.Receiver;
using Miracast.Receiver.Windows;

namespace Miracast.Avalonia.Windows;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args) => BuildAvaloniaApp()
        .StartWithClassicDesktopLifetime(args);

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure(() => new App(services =>
            {
                services.AddSingleton<IMiracastReceiverService, MiracastReceiverService>();
                services.AddSingleton<IVideoRenderer, VideoRenderer>();
            }))
            .UseWin32()
            .UseSkia()
            .WithInterFont()
            .LogToTrace();
}
