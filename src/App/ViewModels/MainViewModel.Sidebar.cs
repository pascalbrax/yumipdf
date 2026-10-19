using CommunityToolkit.Mvvm.ComponentModel;

namespace Pdf26.ViewModels;

/// <summary>The panels that live in the left sidebar - one at a time.</summary>
public enum SidebarPanel
{
    None,
    Thumbnails,
    Bookmarks,
    Tools,
    Form,
    Search,
    Recent,
}

/// <summary>
/// The left sidebar holds one panel at a time: opening any of Miniature, Indice, Strumenti,
/// Compila modulo, Cerca or Recenti closes whichever other one is open (and so un-presses its
/// toolbar button, which is bound to the same flag). Each panel's own <c>On...Changed</c> calls
/// <see cref="CloseOtherPanels"/> when it turns on.
/// </summary>
public partial class MainViewModel
{
    /// <summary>True while panels are being closed because another one opened - not the user's own choice, so it must not be written to settings.</summary>
    private bool _excludingPanels;

    /// <summary>The user's choice for the recent-files panel (toolbar "Recenti" button). Needs no document: it is how one gets the first one.</summary>
    [ObservableProperty]
    public partial bool IsRecentPanelOpen { get; set; }

    partial void OnIsRecentPanelOpenChanged(bool value)
    {
        if (value)
        {
            CloseOtherPanels(SidebarPanel.Recent);
        }

        OnPropertyChanged(nameof(IsLeftPaneOpen));
        OnPropertyChanged(nameof(LeftPaneWidth));
    }

    private void CloseOtherPanels(SidebarPanel keep)
    {
        _excludingPanels = true;
        try
        {
            if (keep != SidebarPanel.Thumbnails) IsThumbnailPanelOpen = false;
            if (keep != SidebarPanel.Bookmarks) IsBookmarkPanelOpen = false;
            if (keep != SidebarPanel.Tools) IsToolsPanelOpen = false;
            if (keep != SidebarPanel.Form) IsFormPanelOpen = false;
            if (keep != SidebarPanel.Search) IsSearchOpen = false;
            if (keep != SidebarPanel.Recent) IsRecentPanelOpen = false;
        }
        finally
        {
            _excludingPanels = false;
        }
    }

    /// <summary>Closes whichever panel is open (the sidebar's edge handle). Unlike <see cref="CloseOtherPanels"/> this is the user's choice, so thumbnails/index remember it.</summary>
    public void CloseSidebar()
    {
        IsThumbnailPanelOpen = false;
        IsBookmarkPanelOpen = false;
        IsToolsPanelOpen = false;
        IsFormPanelOpen = false;
        IsSearchOpen = false;
        IsRecentPanelOpen = false;
    }
}
