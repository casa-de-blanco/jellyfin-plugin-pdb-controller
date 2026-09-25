using Jellyfin.Plugin.PdbController.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.PdbController;

/// <summary>
/// Holds a Kubernetes PodDisruptionBudget open while Jellyfin is doing something a
/// node drain would interrupt.
/// </summary>
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    /// <summary>
    /// Initializes a new instance of the <see cref="Plugin"/> class.
    /// </summary>
    /// <param name="applicationPaths">Server application paths.</param>
    /// <param name="xmlSerializer">Serializer used for the configuration file.</param>
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    /// <summary>
    /// Gets the running instance. The hosted service reads configuration through
    /// this rather than taking the plugin as a dependency, because the plugin is
    /// constructed by the plugin manager and not by the DI container.
    /// </summary>
    public static Plugin? Instance { get; private set; }

    /// <inheritdoc />
    public override string Name => "PDB Controller";

    /// <inheritdoc />
    public override Guid Id => Guid.Parse("4720d2bd-83fc-46c3-84ca-3ae264678a17");

    /// <inheritdoc />
    public override string Description =>
        "Holds a Kubernetes PodDisruptionBudget while selected scheduled tasks run or selected users stream.";

    /// <inheritdoc />
    public IEnumerable<PluginPageInfo> GetPages()
    {
        yield return new PluginPageInfo
        {
            Name = Name,
            EmbeddedResourcePath = GetType().Namespace + ".Configuration.configPage.html",
        };
    }
}
