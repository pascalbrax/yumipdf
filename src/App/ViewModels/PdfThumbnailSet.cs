using Microsoft.UI.Xaml.Media.Imaging;

namespace Pdf26.ViewModels;

/// <summary>One page's thumbnail: its shape is known up front, its <see cref="Bitmap"/> arrives later.</summary>
public sealed class PdfThumbnail
{
    public PdfThumbnail(int index, double aspectRatio)
    {
        Index = index;
        AspectRatio = aspectRatio;
    }

    public int Index { get; }

    /// <summary>Page height divided by page width, so the placeholder already has the right shape before any bitmap exists.</summary>
    public double AspectRatio { get; }

    /// <summary><c>null</c> until <c>MainViewModel</c>'s background generation reaches this page.</summary>
    public WriteableBitmap? Bitmap { get; internal set; }
}

/// <summary>
/// The thumbnails of one document, one entry per page. Deliberately a plain array plus a single
/// change event instead of an observable collection of observable items: a document can have
/// thousands of pages, and the Cover Flow strip only ever shows a handful of them, so per-item
/// change subscriptions would cost far more than they give.
/// </summary>
public sealed class PdfThumbnailSet
{
    private readonly PdfThumbnail[] _items;

    public PdfThumbnailSet(IEnumerable<(double Width, double Height)> pageSizes)
    {
        _items = pageSizes
            .Select((size, index) => new PdfThumbnail(index, size.Width > 0 ? size.Height / size.Width : 1.4142))
            .ToArray();
    }

    public int Count => _items.Length;

    public PdfThumbnail this[int index] => _items[index];

    /// <summary>Raised on the UI thread each time a page's thumbnail bitmap is (re)generated.</summary>
    public event EventHandler<int>? ThumbnailChanged;

    internal void SetBitmap(int index, WriteableBitmap bitmap)
    {
        _items[index].Bitmap = bitmap;
        ThumbnailChanged?.Invoke(this, index);
    }
}
