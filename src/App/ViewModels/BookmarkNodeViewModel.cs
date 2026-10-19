using System.ComponentModel;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using PdfEngine;
using Windows.UI.Text;

namespace Pdf26.ViewModels;

/// <summary>One entry of the document index (outline), shaped for the sidebar's <c>TreeView</c>.</summary>
public sealed class BookmarkNodeViewModel : INotifyPropertyChanged
{
    private bool _isCurrent;
    private bool _isExpanded;

    public BookmarkNodeViewModel(PdfBookmark bookmark, int pageCount, BookmarkNodeViewModel? parent = null)
    {
        Parent = parent;
        Level = parent is null ? 0 : parent.Level + 1;
        Title = string.IsNullOrWhiteSpace(bookmark.Title) ? "(senza titolo)" : bookmark.Title.Trim();
        // A destination past the last page (a damaged or truncated file) leads nowhere, same as no destination at all.
        PageIndex = bookmark.PageIndex >= 0 && bookmark.PageIndex < pageCount ? bookmark.PageIndex : -1;
        _isExpanded = bookmark.IsInitiallyOpen;
        Children = bookmark.Children.Select(child => new BookmarkNodeViewModel(child, pageCount, this)).ToList();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Title { get; }

    public BookmarkNodeViewModel? Parent { get; }

    /// <summary>Nesting depth: 0 for a top-level entry.</summary>
    public int Level { get; }

    /// <summary>0-based page the entry leads to, or -1 when there is nowhere to go (a heading-only entry, or a link outside this document).</summary>
    public int PageIndex { get; }

    public bool CanNavigate => PageIndex >= 0;

    /// <summary>1-based page number shown beside the title; empty for an entry that leads nowhere.</summary>
    public string PageText => CanNavigate ? (PageIndex + 1).ToString() : string.Empty;

    public IReadOnlyList<BookmarkNodeViewModel> Children { get; }

    /// <summary>
    /// Whether this is the entry the page being read belongs to (see
    /// <c>MainViewModel.CurrentBookmark</c>) - drawn highlighted.
    /// </summary>
    public bool IsCurrent
    {
        get => _isCurrent;
        set
        {
            if (_isCurrent == value)
            {
                return;
            }

            _isCurrent = value;
            Raise(nameof(IsCurrent));
            Raise(nameof(TitleFontWeight));
        }
    }

    /// <summary>Starts as the document asked for; two-way bound to the tree item, and set here to reveal the current entry inside a collapsed parent.</summary>
    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (_isExpanded == value)
            {
                return;
            }

            _isExpanded = value;
            Raise(nameof(IsExpanded));
        }
    }

    // ---- Look: the hierarchy reads from type, not just from indentation --------------------

    /// <summary>Top-level entries (chapters) are a notch larger than everything nested under them.</summary>
    public double TitleFontSize => Level == 0 ? 14 : 13;

    /// <summary>Chapters semibold, nested entries regular, the current entry bold at any level.</summary>
    public FontWeight TitleFontWeight => IsCurrent ? FontWeights.Bold : Level == 0 ? FontWeights.SemiBold : FontWeights.Normal;

    /// <summary>Entries that lead nowhere read as plain headings (dimmer than the ones that can be clicked through), and deep levels step back a little.</summary>
    public double TitleOpacity => !CanNavigate ? 0.6 : Level >= 2 ? 0.85 : 1.0;

    /// <summary>A little air above and below each chapter so they separate from their neighbours.</summary>
    public Thickness RowMargin => Level == 0 ? new Thickness(0, 3, 0, 3) : new Thickness(0);

    private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
