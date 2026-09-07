using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;
using PdfEngine.Native;

namespace PdfEngine;

/// <summary>
/// Process-wide PDFium library lifecycle, and the single dedicated thread every PdfEngine
/// type routes native calls through.
/// </summary>
/// <remarks>
/// PDFium (built without its optional multi-threading support, which this project does not
/// enable) is not safe to call into from more than one thread at a time - not even for
/// unrelated documents/pages. A plain lock around every call (this type's original design)
/// only serializes access, it does not pin PDFium to one OS thread - and callers here always
/// reach it via <c>Task.Run</c>, which hands each call to whatever threadpool worker happens
/// to be free, a different one from call to call. That mismatch corrupted the native heap
/// (<c>System.ExecutionEngineException</c> with no managed stack; separately, the same crash
/// signature - access violation at one fixed offset inside coreclr.dll - in Windows' own
/// Application-Error log across every repro) even after every actual page/page-view lifecycle
/// bug found in <see cref="Forms.PdfFormFiller"/> had already been fixed - confirmed by testing
/// live against a real form. Routing every call through one persistent thread instead of a
/// lock removes the cross-thread migration entirely, which a lock alone cannot.
/// </remarks>
public static class PdfiumRuntime
{
    private static readonly BlockingCollection<Action> Queue = new();
    private static readonly Thread Worker;

    [ThreadStatic]
    private static bool _isPdfiumThread;

    private static bool _initialized;

    static PdfiumRuntime()
    {
        Worker = new Thread(RunLoop)
        {
            IsBackground = true,
            Name = "PDFium",
        };
        Worker.Start();
    }

    private static void RunLoop()
    {
        _isPdfiumThread = true;
        foreach (var work in Queue.GetConsumingEnumerable())
        {
            work();
        }
    }

    /// <summary>
    /// Runs <paramref name="work"/> on the dedicated PDFium thread, blocking the caller until
    /// it completes (and re-throwing whatever it threw). Safe to call re-entrantly - a call
    /// already running on the PDFium thread (e.g. <see cref="PdfDocument.GetPage"/> invoked
    /// from within another PdfEngine method that is itself mid-<see cref="Invoke{T}"/>) just
    /// runs <paramref name="work"/> inline instead of deadlocking the thread waiting on itself.
    /// </summary>
    public static T Invoke<T>(Func<T> work)
    {
        if (_isPdfiumThread)
        {
            return work();
        }

        var result = default(T)!;
        ExceptionDispatchInfo? error = null;
        using var done = new ManualResetEventSlim(false);

        Queue.Add(() =>
        {
            try
            {
                result = work();
            }
            catch (Exception ex)
            {
                error = ExceptionDispatchInfo.Capture(ex);
            }
            finally
            {
                done.Set();
            }
        });

        done.Wait();
        error?.Throw();
        return result;
    }

    /// <summary>Same as <see cref="Invoke{T}"/> for work with no result.</summary>
    public static void Invoke(Action work)
        => Invoke<object?>(() =>
        {
            work();
            return null;
        });

    public static void EnsureInitialized()
    {
        if (_initialized)
        {
            return;
        }

        Invoke(() =>
        {
            if (_initialized)
            {
                return;
            }

            PdfiumNative.FPDF_InitLibrary();
            _initialized = true;
        });
    }
}
