using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Pdf26.Services;

namespace Pdf26.ViewModels;

/// <summary>One line of the recent-files panel.</summary>
/// <param name="Path">The document's path or URL - what is opened and what the list is keyed on.</param>
/// <param name="Name">The file name (for a URL, its last segment, else the whole address).</param>
/// <param name="Folder">Where it is: the folder (for a URL, its host).</param>
public sealed record RecentFileEntry(string Path, string Name, string Folder)
{
    public static RecentFileEntry From(string value)
    {
        var trimmed = value.TrimEnd('\\', '/');
        var name = System.IO.Path.GetFileName(trimmed);
        string folder;
        if (Uri.TryCreate(value, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
        {
            folder = uri.Host;
            name = string.IsNullOrEmpty(name) ? value : name;
        }
        else
        {
            folder = System.IO.Path.GetDirectoryName(trimmed) ?? string.Empty;
            name = string.IsNullOrEmpty(name) ? value : name;
        }

        return new RecentFileEntry(value, name, folder);
    }
}

/// <summary>The recent-files list: the last documents opened, newest first, remembered between sessions and shown in the sidebar's "Recenti" panel.</summary>
public partial class MainViewModel
{
    /// <summary>How many recent documents are kept.</summary>
    public const int MaxRecentFiles = 10;

    /// <summary>What the panel lists, newest first; kept in step with <c>AppSettings.RecentFiles</c>.</summary>
    public ObservableCollection<RecentFileEntry> RecentEntries { get; } = new();

    /// <summary>For the panel's "nothing here yet" line.</summary>
    public bool HasNoRecentFiles => RecentEntries.Count == 0;

    private void RefreshRecentEntries()
    {
        RecentEntries.Clear();
        foreach (var value in _settings.RecentFiles)
        {
            RecentEntries.Add(RecentFileEntry.From(value));
        }

        OnPropertyChanged(nameof(HasNoRecentFiles));
    }

    /// <summary>
    /// Moves a successfully opened document to the top of the list (a document already in it is
    /// moved, not duplicated - paths compare case-insensitively, as Windows does). Only called
    /// after a document really opened: a wrong password or an unreadable file never earns a spot.
    /// </summary>
    private void RegisterRecent(PdfSource source)
    {
        var list = _settings.RecentFiles;
        list.RemoveAll(entry => string.Equals(entry, source.Value, StringComparison.OrdinalIgnoreCase));
        list.Insert(0, source.Value);
        if (list.Count > MaxRecentFiles)
        {
            list.RemoveRange(MaxRecentFiles, list.Count - MaxRecentFiles);
        }

        SaveSettings();
        RefreshRecentEntries();
    }

    /// <summary>Drops an entry (its file can no longer be opened, or the user removed it).</summary>
    public void RemoveRecent(string value)
    {
        if (_settings.RecentFiles.RemoveAll(entry => string.Equals(entry, value, StringComparison.OrdinalIgnoreCase)) > 0)
        {
            SaveSettings();
            RefreshRecentEntries();
        }
    }

    public void ClearRecent()
    {
        if (_settings.RecentFiles.Count > 0)
        {
            _settings.RecentFiles.Clear();
            SaveSettings();
            RefreshRecentEntries();
        }
    }
}
