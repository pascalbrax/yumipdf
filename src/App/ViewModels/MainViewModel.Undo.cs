using CommunityToolkit.Mvvm.Input;

namespace Pdf26.ViewModels;

/// <summary>One thing the user did that can be taken back and done again.</summary>
public sealed class UndoEntry : IDisposable
{
    private readonly IDisposable? _resource;

    /// <param name="description">What it was, for the tooltip ("modifica del testo a pagina 3").</param>
    /// <param name="undo">Puts the document back as it was before.</param>
    /// <param name="redo">Does it again.</param>
    /// <param name="resource">Whatever the entry keeps alive for undo/redo (e.g. a page object taken off the page): disposed when the entry is discarded.</param>
    /// <param name="tag">Lets the view model recognise its own entries (e.g. the creation of a given annotation).</param>
    public UndoEntry(string description, Func<Task> undo, Func<Task> redo, IDisposable? resource = null, object? tag = null)
    {
        Description = description;
        Undo = undo;
        Redo = redo;
        _resource = resource;
        Tag = tag;
    }

    public object? Tag { get; }

    public string Description { get; }

    public Func<Task> Undo { get; }

    public Func<Task> Redo { get; }

    public void Dispose() => _resource?.Dispose();
}

/// <summary>
/// Undo and redo (Ctrl+Z, Ctrl+Y or Ctrl+Maiusc+Z). Each edit the user makes pushes an entry that knows how to
/// reverse and repeat itself; the stack is cleared when another document replaces the open one. Entries are taken
/// back strictly in reverse order - several of them refer to things by position (a page's object or annotation
/// index) that only stays valid that way - so an action that cannot be reversed clears the history instead of
/// leaving entries that would hit the wrong thing (<see cref="ClearUndoHistory"/>).
/// </summary>
public partial class MainViewModel
{
    private const int MaxUndoEntries = 100;

    private readonly List<UndoEntry> _undoStack = new();
    private readonly List<UndoEntry> _redoStack = new();
    private readonly SemaphoreSlim _undoGate = new(1, 1);

    public bool CanUndo => _undoStack.Count > 0;

    public bool CanRedo => _redoStack.Count > 0;

    public string UndoToolTip => CanUndo ? $"Annulla {_undoStack[^1].Description} (Ctrl+Z)" : "Annulla (Ctrl+Z)";

    public string RedoToolTip => CanRedo ? $"Ripeti {_redoStack[^1].Description} (Ctrl+Y)" : "Ripeti (Ctrl+Y)";

    /// <summary>Records something the user just did. Starting a new action ends the redo history.</summary>
    private void PushUndo(UndoEntry entry)
    {
        DiscardAll(_redoStack);
        _undoStack.Add(entry);
        if (_undoStack.Count > MaxUndoEntries)
        {
            _undoStack[0].Dispose();
            _undoStack.RemoveAt(0);
        }

        NotifyUndoChanged();
    }

    /// <summary>Forgets every entry: another document replaced the open one, or something happened that undo cannot reverse.</summary>
    private void ClearUndoHistory()
    {
        DiscardAll(_undoStack);
        DiscardAll(_redoStack);
        NotifyUndoChanged();
    }

    private static void DiscardAll(List<UndoEntry> entries)
    {
        foreach (var entry in entries)
        {
            entry.Dispose();
        }

        entries.Clear();
    }

    private void NotifyUndoChanged()
    {
        OnPropertyChanged(nameof(CanUndo));
        OnPropertyChanged(nameof(CanRedo));
        OnPropertyChanged(nameof(UndoToolTip));
        OnPropertyChanged(nameof(RedoToolTip));
        UndoCommand.NotifyCanExecuteChanged();
        RedoCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanUndo))]
    private async Task UndoAsync()
    {
        await _undoGate.WaitAsync().ConfigureAwait(true);
        try
        {
            if (_undoStack.Count == 0)
            {
                return;
            }

            var entry = _undoStack[^1];
            _undoStack.RemoveAt(_undoStack.Count - 1);
            NotifyUndoChanged();
            try
            {
                SelectedTextLine = null;
                SelectAnnotation(null);
                await entry.Undo().ConfigureAwait(true);
                _redoStack.Add(entry);
            }
            catch (Exception ex)
            {
                StatusMessage = $"Impossibile annullare: {ex.Message}";
                entry.Dispose();
            }

            NotifyUndoChanged();
        }
        finally
        {
            _undoGate.Release();
        }
    }

    [RelayCommand(CanExecute = nameof(CanRedo))]
    private async Task RedoAsync()
    {
        await _undoGate.WaitAsync().ConfigureAwait(true);
        try
        {
            if (_redoStack.Count == 0)
            {
                return;
            }

            var entry = _redoStack[^1];
            _redoStack.RemoveAt(_redoStack.Count - 1);
            NotifyUndoChanged();
            try
            {
                SelectedTextLine = null;
                SelectAnnotation(null);
                await entry.Redo().ConfigureAwait(true);
                _undoStack.Add(entry);
            }
            catch (Exception ex)
            {
                StatusMessage = $"Impossibile ripetere: {ex.Message}";
                entry.Dispose();
            }

            NotifyUndoChanged();
        }
        finally
        {
            _undoGate.Release();
        }
    }
}
