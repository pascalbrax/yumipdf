using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Runtime.InteropServices.WindowsRuntime;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml.Media.Imaging;
using PdfEngine;
using PdfEngine.Organize;

namespace Pdf26.ViewModels;

/// <summary>One page in the "Organizza pagine" grid: where it was in the document, where it is now, and its thumbnail.</summary>
public sealed partial class OrganizePageItem : ObservableObject
{
    public OrganizePageItem(int sourceIndex, double aspectRatio, WriteableBitmap? thumbnail)
    {
        SourceIndex = sourceIndex;
        AspectRatio = aspectRatio;
        Thumbnail = thumbnail;
        Position = sourceIndex + 1;
    }

    /// <summary>0-based position in the document as it was when the window opened.</summary>
    public int SourceIndex { get; }

    /// <summary>Page height divided by width, as the page is in the document (before any turn made in this window).</summary>
    public double AspectRatio { get; }

    /// <summary>Quarter turns clockwise made to this page in this window (0..3), on top of the rotation the page already has.</summary>
    public int Turns { get; private set; }

    /// <summary>
    /// The angle, in degrees, the thumbnail is shown at. Unlike <see cref="Turns"/> it does not wrap around: after three clockwise turns
    /// it is 270 and a fourth makes it 360, so the picture keeps turning the same way instead of spinning back (the animation
    /// goes from the old angle to the new one).
    /// </summary>
    public float Angle { get; private set; }

    /// <summary>
    /// Turns the page to <paramref name="turns"/> (0..3) by the shortest way: a quarter turn either way, or a half turn.
    /// Nothing is recorded in the undo history here - the view model does that once per user action.
    /// </summary>
    internal void SetTurns(int turns)
    {
        turns = ((turns % 4) + 4) % 4;
        var delta = ((turns - Turns) % 4 + 4) % 4;
        if (delta == 3)
        {
            delta = -1;
        }

        if (delta == 0)
        {
            return;
        }

        Turns = turns;
        Angle += delta * 90f;
        OnPropertyChanged(nameof(Turns));
        OnPropertyChanged(nameof(Angle));
        OnPropertyChanged(nameof(BoxWidth));
        OnPropertyChanged(nameof(BoxHeight));
        OnPropertyChanged(nameof(ImageWidth));
        OnPropertyChanged(nameof(ImageHeight));
        OnPropertyChanged(nameof(ImageCenter));
        OnPropertyChanged(nameof(Caption));
    }

    /// <summary>Page height divided by width as it shows now: upside down in ratio after a quarter turn.</summary>
    private double ShownAspectRatio => Turns % 2 == 0 ? AspectRatio : 1 / AspectRatio;

    /// <summary>Size, in pixels, of the box that holds the thumbnail in the grid: the page's shape, as it is turned now, fitted in 148 x 196.</summary>
    public double BoxWidth => Math.Min(148, 196 / ShownAspectRatio);

    public double BoxHeight => BoxWidth * ShownAspectRatio;

    // The picture itself keeps the page's own shape and is turned by Angle around its middle, so on a quarter turn its width is the
    // box's height (less the 1 px border on each side).
    public double ImageWidth => (Turns % 2 == 0 ? BoxWidth : BoxHeight) - 2;

    public double ImageHeight => (Turns % 2 == 0 ? BoxHeight : BoxWidth) - 2;

    public System.Numerics.Vector3 ImageCenter => new((float)ImageWidth / 2, (float)ImageHeight / 2, 0);

    /// <summary><c>null</c> until the background rendering reaches this page (or the main window already had one).</summary>
    [ObservableProperty]
    public partial WriteableBitmap? Thumbnail { get; set; }

    /// <summary>1-based position among the pages that are kept, as things stand now.</summary>
    [ObservableProperty]
    public partial int Position { get; set; }

    /// <summary>Set when rendering this page's thumbnail failed: it is not tried again (the number is shown instead).</summary>
    internal bool ThumbnailFailed { get; set; }

    public bool IsMoved => Position - 1 != SourceIndex;

    /// <summary>"3", or "3 (era 7)" for a page that is no longer where it was, plus "↻ 90°" (or 180°, "↺ 90°") for one that is turned.</summary>
    public string Caption
    {
        get
        {
            var text = IsMoved ? $"{Position}  (era {SourceIndex + 1})" : Position.ToString();
            return Turns switch
            {
                1 => text + "  ↻ 90°",
                2 => text + "  ↻ 180°",
                3 => text + "  ↺ 90°",
                _ => text,
            };
        }
    }

    public bool HasNoThumbnail => Thumbnail is null;

    partial void OnPositionChanged(int value)
    {
        OnPropertyChanged(nameof(IsMoved));
        OnPropertyChanged(nameof(Caption));
    }

    partial void OnThumbnailChanged(WriteableBitmap? value) => OnPropertyChanged(nameof(HasNoThumbnail));
}

/// <summary>
/// The state of the "Organizza pagine" window: a working list of the document's pages that the user reorders and removes by
/// dragging, with its own undo/redo, and the thumbnails to draw them. The document itself is never touched here: only
/// <see cref="SaveAsync"/> applies the list (<see cref="PdfPageOrganizer"/>), and to a <em>new</em> file.
/// </summary>
public sealed partial class OrganizePagesViewModel : ObservableObject
{
    private const int ThumbnailLongSidePixels = 360;

    private readonly byte[] _pdf;
    private readonly string? _password;
    private readonly PdfDocument _working;
    private readonly List<OrganizePageItem> _all;
    private readonly List<Snapshot> _history = new();
    private int _historyIndex;
    private bool _applyingHistory;

    /// <summary>One undo step: the pages kept, in order (by source index), and the turns of every page of the document (indexed by source index).</summary>
    private sealed record Snapshot(int[] Order, int[] Turns)
    {
        public bool SameAs(Snapshot other) => Order.AsSpan().SequenceEqual(other.Order) && Turns.AsSpan().SequenceEqual(other.Turns);
    }

    private readonly HashSet<OrganizePageItem> _wanted = new();
    private readonly CancellationTokenSource _loaderCts = new();
    private Task _loader = Task.CompletedTask;

    private OrganizePagesViewModel(byte[] pdf, string? password, string title, PdfDocument working, List<OrganizePageItem> items, int startPage)
    {
        _pdf = pdf;
        _password = password;
        Title = title;
        _working = working;
        _all = items;
        StartPageIndex = startPage;
        Pages = new ObservableCollection<OrganizePageItem>(items);
        Pages.CollectionChanged += OnPagesChanged;
        _history.Add(CurrentSnapshot());
    }

    /// <summary>The document's name, for the suggested name of the new file.</summary>
    public string Title { get; }

    /// <summary>The page the main window was showing: the grid scrolls to it when it opens.</summary>
    public int StartPageIndex { get; }

    /// <summary>The pages that are kept, in their new order. Changed by the grid's own drag and drop (reordering) and by <see cref="Remove"/>.</summary>
    public ObservableCollection<OrganizePageItem> Pages { get; }

    public int TotalPageCount => _all.Count;

    public int RemovedCount => _all.Count - Pages.Count;

    public int RotatedCount => Pages.Count(page => page.Turns != 0);

    /// <summary>"12 pagine · 3 eliminate · 2 ruotate": what the document will have, and what is being changed.</summary>
    public string Summary
    {
        get
        {
            var text = Pages.Count == 1 ? "1 pagina" : $"{Pages.Count} pagine";
            text += RemovedCount switch
            {
                0 => string.Empty,
                1 => " · 1 eliminata",
                _ => $" · {RemovedCount} eliminate",
            };
            return RotatedCount switch
            {
                0 => text,
                1 => text + " · 1 ruotata",
                var count => text + $" · {count} ruotate",
            };
        }
    }

    /// <summary>Whether the pages differ from the document's: only then is there anything to save.</summary>
    public bool HasChanges
        => Pages.Count != _all.Count
           || Pages.Select((page, index) => page.SourceIndex != index).Any(different => different)
           || Pages.Any(page => page.Turns != 0);

    public bool CanUndo => _historyIndex > 0;

    public bool CanRedo => _historyIndex < _history.Count - 1;

    /// <summary>Opens a copy of the document's bytes (the file as it would be saved now) and prepares the page list. Heavy for a big file, hence async.</summary>
    /// <param name="existingThumbnails">The main window's thumbnails, when it has them: pages that already have one are shown at once instead of being rendered again.</param>
    public static async Task<OrganizePagesViewModel> CreateAsync(byte[] pdf, string? password, string title, PdfThumbnailSet? existingThumbnails, int startPage)
    {
        var (working, sizes) = await Task.Run(() =>
        {
            var document = PdfDocument.OpenFromBytes(pdf, password);
            try
            {
                var list = new List<(double Width, double Height)>(document.PageCount);
                for (var index = 0; index < document.PageCount; index++)
                {
                    using var page = document.GetPage(index);
                    list.Add(page.SizeInPoints);
                }

                return (document, list);
            }
            catch
            {
                document.Dispose();
                throw;
            }
        }).ConfigureAwait(true);

        var items = sizes
            .Select((size, index) => new OrganizePageItem(
                index,
                size.Width > 0 ? size.Height / size.Width : 1.4142,
                existingThumbnails is { } set && index < set.Count ? set[index].Bitmap : null))
            .ToList();

        var viewModel = new OrganizePagesViewModel(pdf, password, title, working, items, Math.Clamp(startPage, 0, Math.Max(0, items.Count - 1)));
        viewModel._loader = viewModel.LoadThumbnailsAsync(viewModel._loaderCts.Token);
        return viewModel;
    }

    // ---- editing ---------------------------------------------------------------------

    /// <summary>Takes the pages out of the list (the trash, the Delete key). The last page cannot be removed: a document needs one.</summary>
    public void Remove(IEnumerable<OrganizePageItem> items)
    {
        foreach (var item in items.ToList())
        {
            if (Pages.Count <= 1)
            {
                break;
            }

            Pages.Remove(item);
        }

        Commit();
    }

    /// <summary>A quarter turn for each of the pages (the rotate buttons), one undo step for all of them.</summary>
    /// <param name="clockwise"><c>true</c> to turn them to the right, <c>false</c> to the left.</param>
    public void Rotate(IEnumerable<OrganizePageItem> items, bool clockwise)
    {
        foreach (var item in items.ToList())
        {
            item.SetTurns(item.Turns + (clockwise ? 1 : 3));
        }

        Commit();
    }

    /// <summary>Records the current list (and the turns) as one undo step, if it differs from the last one - called when a drag ends.</summary>
    public void Commit()
    {
        if (_applyingHistory)
        {
            return;
        }

        var snapshot = CurrentSnapshot();
        if (snapshot.SameAs(_history[_historyIndex]))
        {
            return;
        }

        _history.RemoveRange(_historyIndex + 1, _history.Count - _historyIndex - 1);
        _history.Add(snapshot);
        _historyIndex = _history.Count - 1;
        NotifyHistory();
    }

    public void Undo()
    {
        if (CanUndo)
        {
            Go(_historyIndex - 1);
        }
    }

    public void Redo()
    {
        if (CanRedo)
        {
            Go(_historyIndex + 1);
        }
    }

    /// <summary>Back to the document's own order, with every page and none turned; one undo step.</summary>
    public void Reset()
    {
        ApplySnapshot(new Snapshot(Enumerable.Range(0, _all.Count).ToArray(), new int[_all.Count]));
        Commit();
    }

    private void Go(int historyIndex)
    {
        _historyIndex = historyIndex;
        ApplySnapshot(_history[historyIndex]);
        NotifyHistory();
    }

    private void ApplySnapshot(Snapshot snapshot)
    {
        for (var index = 0; index < _all.Count; index++)
        {
            _all[index].SetTurns(snapshot.Turns[index]);
        }

        ApplyOrder(snapshot.Order);
    }

    /// <summary>
    /// Makes <see cref="Pages"/> match <paramref name="order"/> with the fewest moves - removing, re-inserting and moving the same
    /// items, never rebuilding the list - so the grid animates the change instead of redrawing.
    /// </summary>
    private void ApplyOrder(int[] order)
    {
        _applyingHistory = true;
        try
        {
            var keep = order.ToHashSet();
            for (var index = Pages.Count - 1; index >= 0; index--)
            {
                if (!keep.Contains(Pages[index].SourceIndex))
                {
                    Pages.RemoveAt(index);
                }
            }

            for (var target = 0; target < order.Length; target++)
            {
                var item = _all[order[target]];
                if (target < Pages.Count && ReferenceEquals(Pages[target], item))
                {
                    continue;
                }

                var current = Pages.IndexOf(item);
                if (current < 0)
                {
                    Pages.Insert(target, item);
                }
                else
                {
                    Pages.Move(current, target);
                }
            }
        }
        finally
        {
            _applyingHistory = false;
        }

        Renumber();
    }

    private int[] CurrentOrder() => Pages.Select(page => page.SourceIndex).ToArray();

    private Snapshot CurrentSnapshot() => new(CurrentOrder(), _all.Select(page => page.Turns).ToArray());

    private void OnPagesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (!_applyingHistory)
        {
            Renumber();
        }
    }

    private void Renumber()
    {
        for (var index = 0; index < Pages.Count; index++)
        {
            Pages[index].Position = index + 1;
        }

        OnPropertyChanged(nameof(Summary));
        OnPropertyChanged(nameof(RemovedCount));
        OnPropertyChanged(nameof(RotatedCount));
        OnPropertyChanged(nameof(HasChanges));
    }

    private void NotifyHistory()
    {
        OnPropertyChanged(nameof(CanUndo));
        OnPropertyChanged(nameof(CanRedo));
        OnPropertyChanged(nameof(Summary));
        OnPropertyChanged(nameof(RotatedCount));
        OnPropertyChanged(nameof(HasChanges));
    }

    // ---- saving ----------------------------------------------------------------------

    /// <summary>Writes the pages, in their new order, turned as they are shown and without the removed ones, to a new file. The document that is open and the original file stay as they are.</summary>
    public Task SaveAsync(string destinationPath)
    {
        var order = CurrentOrder();
        var turns = Pages.Select(page => page.Turns).ToArray();
        return Task.Run(() => File.WriteAllBytes(destinationPath, PdfPageOrganizer.Apply(_pdf, order, _password, turns)));
    }

    // ---- thumbnails ------------------------------------------------------------------

    /// <summary>The grid has put this page on screen (<paramref name="visible"/>) or taken it off: pages on screen get their thumbnail first.</summary>
    public void SetVisible(OrganizePageItem item, bool visible)
    {
        if (visible)
        {
            _wanted.Add(item);
        }
        else
        {
            _wanted.Remove(item);
        }
    }

    private OrganizePageItem? NextWithoutThumbnail()
    {
        foreach (var item in _wanted.OrderBy(page => page.SourceIndex))
        {
            if (item.Thumbnail is null && !item.ThumbnailFailed)
            {
                return item;
            }
        }

        return _all.FirstOrDefault(page => page.Thumbnail is null && !page.ThumbnailFailed);
    }

    private async Task LoadThumbnailsAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested && NextWithoutThumbnail() is { } item)
        {
            var width = item.AspectRatio >= 1 ? Math.Max(1, (int)Math.Round(ThumbnailLongSidePixels / item.AspectRatio)) : ThumbnailLongSidePixels;
            var height = item.AspectRatio >= 1 ? ThumbnailLongSidePixels : Math.Max(1, (int)Math.Round(ThumbnailLongSidePixels * item.AspectRatio));
            try
            {
                var bgra = await Task.Run(() =>
                {
                    using var page = _working.GetPage(item.SourceIndex);
                    return page.RenderToBgra(width, height);
                }, token).ConfigureAwait(true);

                var bitmap = new WriteableBitmap(width, height);
                using (var stream = bitmap.PixelBuffer.AsStream())
                {
                    stream.Write(bgra, 0, bgra.Length);
                }

                item.Thumbnail = bitmap;
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception)
            {
                // A thumbnail is decoration: the page keeps its number and can still be moved or removed.
                item.ThumbnailFailed = true;
            }
        }
    }

    /// <summary>Stops the thumbnail rendering, waits for the page it was on, and closes the working copy.</summary>
    public async Task CloseAsync()
    {
        _loaderCts.Cancel();
        try
        {
            await _loader.ConfigureAwait(true);
        }
        catch
        {
            // Cancelled: nothing to report.
        }

        _working.Dispose();
        _loaderCts.Dispose();
    }
}
