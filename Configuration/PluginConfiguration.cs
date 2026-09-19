using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.AnimeSeasonCollections.Configuration;

/// <summary>
/// Plugin settings.
/// </summary>
public sealed class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    /// Gets or sets the Jellyfin library item IDs that are explicitly included.
    /// An empty array means all libraries are eligible unless excluded below.
    /// </summary>
    public string[] IncludedLibraryIds { get; set; } = [];

    /// <summary>
    /// Gets or sets the Jellyfin library item IDs that are explicitly excluded.
    /// Exclusion always wins over inclusion.
    /// </summary>
    public string[] ExcludedLibraryIds { get; set; } = [];

    /// <summary>
    /// Gets or sets a value indicating whether the parent Series should also be
    /// added to each generated collection for clients that do not render Season
    /// items inside collections. Enabled by default for broad client compatibility.
    /// </summary>
    public bool IncludeParentSeriesForClientCompatibility { get; set; } = true;
}
