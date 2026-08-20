using System;
using Microsoft.Extensions.DependencyInjection;
using Miracast.Receiver;
using LinuxReceiverService = Miracast.Receiver.Linux.MiracastReceiverService;
using LinuxVideoRenderer = Miracast.Receiver.Linux.VideoRenderer;
using WindowsReceiverService = Miracast.Receiver.Windows.MiracastReceiverService;
using WindowsVideoRenderer = Miracast.Receiver.Windows.VideoRenderer;

namespace Miracast.Avalonia;

internal static class MiracastServiceCollectionExtensions
{
    public static IServiceCollection AddMiracastReceiver(this IServiceCollection services)
    {
        if (OperatingSystem.IsWindows())
        {
            if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 18362))
                throw new PlatformNotSupportedException("The Windows receiver requires Windows 10 version 1903 or newer.");

            services.AddSingleton<IMiracastReceiverService, WindowsReceiverService>();
            services.AddSingleton<IVideoRenderer, WindowsVideoRenderer>();
        }

        if (OperatingSystem.IsLinux())
        {
            services.AddSingleton<IMiracastReceiverService, LinuxReceiverService>();
            services.AddSingleton<IVideoRenderer, LinuxVideoRenderer>();
        }

        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux())
            throw new PlatformNotSupportedException("The Miracast receiver supports Windows and Linux only.");

        return services;
    }
}
