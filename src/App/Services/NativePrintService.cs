using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Printing;
using Microsoft.UI.Xaml;
using Printing;
using Windows.Foundation;
using Windows.Graphics.DirectX;
using Windows.Graphics.Printing;
using Microsoft.UI;
using WinRT.Interop;

namespace Pdf26.Services;

/// <summary>Owns one native Windows print experience, including its asynchronous lifetime.</summary>
public sealed class NativePrintService(Window window) : IDisposable
{
    private Session? _session;
    private Session? _previousSession;
    private bool _disposed;
    public bool IsBusy => _session is not null;
    public event Action? StateChanged;
    public event Action<string>? Failed;
    public event Action<string>? Completed;

    /// <summary>Takes ownership of snapshot, including when opening the system UI fails.</summary>
    public async Task ShowAsync(PdfPrintSnapshot snapshot, string title)
    {
        if (_disposed || IsBusy) { snapshot.Dispose(); return; }
        Session? session = null;
        try
        {
            if (!PrintManager.IsSupported())
                throw new NotSupportedException("La stampa non è disponibile su questo dispositivo.");
            _previousSession?.DisposeCanvas();
            _previousSession = null;
            session = new Session(this, snapshot, title, window);
            _session = session;
            StateChanged?.Invoke();
            var shown = await PrintManagerInterop.ShowPrintUIForWindowAsync(WindowNative.GetWindowHandle(window));
            // Windows can return false even after requesting a source. In that case the
            // task's Completed event, rather than this return value, owns its lifetime.
            if (!session.TaskRequested)
            {
                Release(session);
                if (!shown) Failed?.Invoke("Impossibile aprire il pannello di stampa di Windows.");
            }
        }
        catch
        {
            if (session is not null) Release(session);
            else snapshot.Dispose();
            throw;
        }
    }

    private void Release(Session session)
    {
        session.Dispose();
        if (ReferenceEquals(_session, session))
        {
            // Keep a managed reference until the next print invocation, as recommended by
            // Win2D. Windows may already have closed its source by Completed; all event
            // unsubscriptions must therefore also tolerate RO_E_CLOSED.
            _previousSession = session;
            _session = null;
            StateChanged?.Invoke();
        }
    }

    public void Dispose()
    {
        _disposed = true;
        if (_session is { } session) Release(session);
        _previousSession?.DisposeCanvas();
        _previousSession = null;
    }

    private sealed class Session : IDisposable
    {
        private readonly NativePrintService _owner;
        private readonly Window _window;
        private readonly PdfPrintSnapshot _snapshot;
        private readonly string _title;
        private readonly PrintManager _manager;
        private readonly CanvasPrintDocument _canvas;
        private readonly CancellationTokenSource _cancellation = new();
        private readonly SemaphoreSlim _renderGate = new(1, 1);
        private PrintTask? _task;
        private int _operations;
        private bool _closing, _released, _failed;
        private bool _disposeCanvas;
        public bool TaskRequested { get; private set; }

        public Session(NativePrintService owner, PdfPrintSnapshot snapshot, string title, Window window)
        {
            _owner = owner;
            _window = window;
            _snapshot = snapshot;
            _title = title;
            _manager = PrintManagerInterop.GetForWindow(WindowNative.GetWindowHandle(window));
            _canvas = new CanvasPrintDocument();
            _canvas.PrintTaskOptionsChanged += OnOptionsChanged;
            _canvas.Preview += OnPreview;
            _canvas.Print += OnPrint;
            _manager.PrintTaskRequested += OnTaskRequested;
        }

        private void OnTaskRequested(PrintManager sender, PrintTaskRequestedEventArgs args)
        {
            TaskRequested = true;
            _task = args.Request.CreatePrintTask(_title, source => source.SetSource(_canvas));
            _task.Completed += OnCompleted;
            var options = _task.Options;
            options.PageRangeOptions.AllowAllPages = true;
            options.PageRangeOptions.AllowCustomSetOfPages = true;
            options.PageRangeOptions.AllowCurrentPage = false;
            options.DisplayedOptions.Clear();
            options.DisplayedOptions.Add(StandardPrintTaskOptions.CustomPageRanges);
            options.DisplayedOptions.Add(StandardPrintTaskOptions.Copies);
            options.DisplayedOptions.Add(StandardPrintTaskOptions.Orientation);
            options.DisplayedOptions.Add(StandardPrintTaskOptions.MediaSize);
            options.DisplayedOptions.Add(StandardPrintTaskOptions.ColorMode);
            options.DisplayedOptions.Add(StandardPrintTaskOptions.Duplex);
            var first = _snapshot.PageSizes[0];
            options.Orientation = first.Width > first.Height ? PrintOrientation.Landscape : PrintOrientation.Portrait;
        }

        private void OnOptionsChanged(CanvasPrintDocument sender, CanvasPrintTaskOptionsChangedEventArgs args)
        {
            try { if (!_closing) sender.SetPageCount((uint)_snapshot.PageCount); }
            catch (Exception ex) when (IsClosed(ex)) { }
            catch (Exception ex) { ReportFailure(ex); }
        }

        private async void OnPreview(CanvasPrintDocument sender, CanvasPreviewEventArgs args)
        {
            if (_closing) return;
            Microsoft.Graphics.Canvas.Printing.CanvasPrintDeferral deferral;
            try { deferral = args.GetDeferral(); }
            catch (Exception ex) when (IsClosed(ex)) { return; }
            catch (Exception ex) { ReportFailure(ex); return; }
            _operations++;
            try
            {
                var description = args.PrintTaskOptions.GetPageDescription(args.PageNumber);
                await DrawPageAsync(args.DrawingSession, (int)args.PageNumber - 1, description, preview: true);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) when (IsClosed(ex)) { }
            catch (Exception ex) { ReportFailure(ex); }
            finally
            {
                FinishOperation(deferral.Complete);
            }
        }

        private async void OnPrint(CanvasPrintDocument sender, CanvasPrintEventArgs args)
        {
            if (_closing) return;
            Microsoft.Graphics.Canvas.Printing.CanvasPrintDeferral deferral;
            try { deferral = args.GetDeferral(); }
            catch (Exception ex) when (IsClosed(ex)) { return; }
            catch (Exception ex) { ReportFailure(ex); return; }
            _operations++;
            try
            {
                // Driver calls can display their own modal Save dialog (Print to PDF).
                // Keep the whole spool operation off the window thread, not just PDFium's
                // rasterization, so both that dialog and the app keep processing messages.
                await Task.Run(async () =>
                {
                    var pages = PrintPageSelection.Resolve(_snapshot.PageCount,
                        args.PrintTaskOptions.CustomPageRanges.Select(range => (range.FirstPageNumber, range.LastPageNumber)));
                    args.Dpi = 300;
                    foreach (var page in pages)
                    {
                        _cancellation.Token.ThrowIfCancellationRequested();
                        var description = args.PrintTaskOptions.GetPageDescription((uint)page);
                        using var drawing = args.CreateDrawingSession();
                        await DrawPageAsync(drawing, page - 1, description, preview: false);
                    }
                }, _cancellation.Token);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) when (IsClosed(ex)) { }
            catch (Exception ex) { ReportFailure(ex); }
            finally
            {
                FinishOperation(deferral.Complete);
            }
        }

        private static bool IsClosed(Exception error)
            => error is ObjectDisposedException || error.HResult is unchecked((int)0x80000013) or unchecked((int)0x80010108);

        private static void IgnoreClosed(Action action)
        {
            try { action(); }
            catch (Exception ex) when (IsClosed(ex)) { }
        }

        private void FinishOperation(Action complete)
        {
            try { IgnoreClosed(complete); }
            catch (Exception ex) { ReportFailure(ex); }
            finally
            {
                _operations--;
                try { ReleaseResourcesIfIdle(); }
                catch (Exception ex) { ReportFailure(ex); }
            }
        }

        private async Task DrawPageAsync(CanvasDrawingSession drawing, int pageIndex, PrintPageDescription description, bool preview)
        {
            await _renderGate.WaitAsync(_cancellation.Token);
            try
            {
                var size = _snapshot.PageSizes[pageIndex];
                var area = description.ImageableRect;
                var placement = PrintPlacement.Fit(size.Width, size.Height,
                    description.PageSize.Width, description.PageSize.Height, area.X, area.Y, area.Width, area.Height);
                var pixels = placement.RasterSize(preview ? 144 : 300, preview ? 1800 : 8192, preview ? 3_000_000 : 20_000_000);
                var bytes = await Task.Run(() => _snapshot.Render(pageIndex, pixels.Width, pixels.Height), _cancellation.Token);
                _cancellation.Token.ThrowIfCancellationRequested();
                // Win2D copies the buffer. Both the managed raster and GPU bitmap are scoped
                // to this page; there is no job-long list of pinned high-resolution buffers.
                using var bitmap = CanvasBitmap.CreateFromBytes(_canvas, bytes, pixels.Width, pixels.Height,
                    DirectXPixelFormat.B8G8R8A8UIntNormalized, 96, CanvasAlphaMode.Ignore);
                drawing.Clear(Colors.White);
                drawing.DrawImage(bitmap, new Rect(placement.X, placement.Y, placement.Width, placement.Height),
                    new Rect(0, 0, pixels.Width, pixels.Height));
            }
            finally { _renderGate.Release(); }
        }

        private void ReportFailure(Exception error)
        {
            if (_failed || _closing) return;
            _failed = true;
            _cancellation.Cancel();
            LogFailure(error);
            _owner.Failed?.Invoke($"Errore nella preparazione della stampa: {error.Message}");
        }

        private void OnCompleted(PrintTask sender, PrintTaskCompletedEventArgs args)
        {
            PrintTaskCompletion completion;
            try { completion = args.Completion; }
            catch (Exception ex) when (IsClosed(ex)) { completion = PrintTaskCompletion.Canceled; }
            _window.DispatcherQueue.TryEnqueue(() =>
            {
                try
                {
                    if (!_closing && !_failed)
                    {
                        if (completion == PrintTaskCompletion.Failed)
                            _owner.Failed?.Invoke("Il sistema non ha potuto completare il lavoro di stampa.");
                        else if (completion == PrintTaskCompletion.Submitted)
                            _owner.Completed?.Invoke("Documento inviato alla stampante.");
                    }
                    _owner.Release(this);
                }
                catch (Exception ex)
                {
                    LogFailure(ex);
                    _owner.Failed?.Invoke($"Errore nella chiusura della stampa: {ex.Message}");
                }
            });
        }

        private static void LogFailure(Exception error)
        {
            try
            {
                var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PDF26", "logs");
                Directory.CreateDirectory(directory);
                File.AppendAllText(Path.Combine(directory, "printing.log"), $"{DateTime.Now:O} {error}{Environment.NewLine}");
            }
            catch { /* Diagnostics must never cause a second printing failure. */ }
        }

        public void Dispose()
        {
            if (_closing) return;
            _closing = true;
            _cancellation.Cancel();
            IgnoreClosed(() => _manager.PrintTaskRequested -= OnTaskRequested);
            if (_task is not null) IgnoreClosed(() => _task.Completed -= OnCompleted);
            IgnoreClosed(() => _canvas.PrintTaskOptionsChanged -= OnOptionsChanged);
            IgnoreClosed(() => _canvas.Preview -= OnPreview);
            IgnoreClosed(() => _canvas.Print -= OnPrint);
            ReleaseResourcesIfIdle();
        }

        private void ReleaseResourcesIfIdle()
        {
            if (!_closing || _operations != 0) return;
            if (_disposeCanvas) { IgnoreClosed(_canvas.Dispose); _disposeCanvas = false; }
            if (_released) return;
            _released = true;
            _snapshot.Dispose();
            _cancellation.Dispose();
            _renderGate.Dispose();
        }

        public void DisposeCanvas()
        {
            _disposeCanvas = true;
            Dispose();
            ReleaseResourcesIfIdle();
        }
    }
}
