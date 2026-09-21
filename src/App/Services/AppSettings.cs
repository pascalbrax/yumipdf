namespace Pdf26.Services;

/// <summary>User-configurable app preferences, persisted as JSON by <see cref="SettingsService"/>.</summary>
public sealed class AppSettings
{
    /// <summary>Background color of the area around PDF pages, as "#AARRGGBB". Default: medium gray.</summary>
    public string PageAreaBackgroundHex { get; set; } = "#FF808080";

    /// <summary>Whether the thumbnail strip on the left is open. Closed on a first run; afterwards it stays as the user left it.</summary>
    public bool ThumbnailPanelOpen { get; set; }
}
