using Jellyfin.Plugin.PdbController.Kubernetes;
using Jellyfin.Plugin.PdbController.Services;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.PdbController;

/// <summary>
/// Registers the plugin's services into the server's generic host.
/// </summary>
/// <remarks>
/// The plugin manager builds this with <c>Activator.CreateInstance</c>, so it must
/// keep a public parameterless constructor. Give it a parameterised one and the
/// failure is silent: the type is skipped and nothing is ever registered.
/// </remarks>
public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    /// <inheritdoc />
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddSingleton<IKubernetesPdbClient, KubernetesPdbClient>();
        serviceCollection.AddHostedService<PdbHoldService>();
    }
}
