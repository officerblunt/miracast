using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Data.Core;
using Avalonia.Data.Core.Plugins;
using System;
using System.Linq;
using Avalonia.Markup.Xaml;
using Microsoft.Extensions.DependencyInjection;
using Miracast.Avalonia.ViewModels;
using Miracast.Avalonia.Views;

namespace Miracast.Avalonia;

public partial class App : Application
{
    private readonly Action<IServiceCollection> _configurePlatformServices;
    private ServiceProvider? _serviceProvider;

    public App(Action<IServiceCollection> configurePlatformServices)
    {
        _configurePlatformServices = configurePlatformServices;
    }

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Avoid duplicate validations from both Avalonia and the CommunityToolkit. 
            // More info: https://docs.avaloniaui.net/docs/guides/development-guides/data-validation#manage-validationplugins
            DisableAvaloniaDataAnnotationValidation();

            var services = new ServiceCollection();
            _configurePlatformServices(services);
            services.AddSingleton<MainWindowViewModel>();
            services.AddSingleton<MainWindow>();
            _serviceProvider = services.BuildServiceProvider(new ServiceProviderOptions
            {
                ValidateOnBuild = true,
                ValidateScopes = true,
            });

            desktop.MainWindow = _serviceProvider.GetRequiredService<MainWindow>();
            desktop.Exit += OnDesktopExit;
        }

        base.OnFrameworkInitializationCompleted();
    }

    private async void OnDesktopExit(object? sender, ControlledApplicationLifetimeExitEventArgs args)
    {
        if (_serviceProvider is null)
            return;

        await _serviceProvider.DisposeAsync();
        _serviceProvider = null;
    }

    private void DisableAvaloniaDataAnnotationValidation()
    {
        // Get an array of plugins to remove
        var dataValidationPluginsToRemove =
            BindingPlugins.DataValidators.OfType<DataAnnotationsValidationPlugin>().ToArray();

        // remove each entry found
        foreach (var plugin in dataValidationPluginsToRemove)
        {
            BindingPlugins.DataValidators.Remove(plugin);
        }
    }
}
