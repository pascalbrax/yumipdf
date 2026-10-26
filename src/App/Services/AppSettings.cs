using System.Text.Json.Serialization;
using PdfEngine.Viewing;

namespace Pdf26.Services;

/// <summary>Which way the mouse wheel turns to zoom in when it is used with CTRL.</summary>
public enum WheelZoomDirection
{
    /// <summary>Wheel turned forward (away from you, "up") zooms in - what most programs do.</summary>
    UpZoomsIn,

    /// <summary>Wheel turned forward zooms out.</summary>
    UpZoomsOut,
}

/// <summary>User-configurable app preferences, persisted as JSON by <see cref="SettingsService"/>.</summary>
public sealed class AppSettings
{
    /// <summary>Background color of the area around PDF pages, as "#AARRGGBB". Default: medium gray.</summary>
    public string PageAreaBackgroundHex { get; set; } = "#FF808080";

    /// <summary>Whether the thumbnail strip on the left is open. Closed on a first run; afterwards it stays as the user left it.</summary>
    public bool ThumbnailPanelOpen { get; set; }

    /// <summary>Whether the index (bookmarks) is the content of the left sidebar. Never true together with <see cref="ThumbnailPanelOpen"/>: the two share the sidebar and exclude each other.</summary>
    public bool BookmarkPanelOpen { get; set; }

    /// <summary>Whether the "Strumenti" panel shows every ready-made stamp, not just the first few. Off on a first run; afterwards it stays as the user left it.</summary>
    public bool MoreStampsExpanded { get; set; }

    /// <summary>Whether placing and moving things on a page snaps them to the dotted grid ("Aggancia alla griglia"). Off on a first run; afterwards it stays as the user left it.</summary>
    public bool SnapToGrid { get; set; }

    /// <summary>How a document with pages of different sizes is shown ("Opzioni"). Saved by name, so the file stays readable and survives reordering the values.</summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public MixedPagesMode MixedPagesMode { get; set; } = MixedPagesMode.CurrentPage;

    /// <summary>Which way the wheel zooms in with CTRL held ("Opzioni"). Saved by name.</summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public WheelZoomDirection WheelZoom { get; set; } = WheelZoomDirection.UpZoomsIn;

    /// <summary>The documents opened most recently (paths or URLs), newest first - see <c>MainViewModel.RegisterRecent</c>.</summary>
    public List<string> RecentFiles { get; set; } = new();
}
