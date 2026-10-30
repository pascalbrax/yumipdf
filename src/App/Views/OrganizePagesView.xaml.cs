using System.Numerics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Pdf26.ViewModels;
using Windows.ApplicationModel.DataTransfer;
using Windows.System;

namespace Pdf26.Views;

/// <summary>
/// The "Organizza pagine" window's contents (see the XAML): the page grid with its drag and drop, and the trash. It holds no
/// document logic - <see cref="OrganizePagesViewModel"/> has the pages, the undo history and the thumbnails, and the main window
/// does the saving (it owns the file picker) - and it reports what the user asked for through events.
/// </summary>
public sealed partial class OrganizePagesView : UserControl
{
    private readonly Brush _trashBackground;
    private readonly Brush _trashIconBrush;

    /// <summary>The pages being dragged right now (set while a drag from the grid is in progress), which is what the trash takes when they are dropped on it.</summary>
    private List<OrganizePageItem>? _dragged;

    public OrganizePagesView()
    {
        InitializeComponent();
        _trashBackground = TrashZone.Background;
        _trashIconBrush = TrashIcon.Foreground;
        // Scale grows from the middle of the trash, not its corner.
        TrashZone.SizeChanged += (_, e) => TrashZone.CenterPoint = new Vector3((float)e.NewSize.Width / 2, (float)e.NewSize.Height / 2, 0);
    }

    public OrganizePagesViewModel? ViewModel { get; private set; }

    public bool IsOpen => ViewModel is not null;

    /// <summary>The user asked to leave (Chiudi, Esc); the window decides whether to ask about unsaved changes.</summary>
    public event EventHandler? CloseRequested;

    /// <summary>The user asked to save to a new file; the argument is whether to open that file afterwards.</summary>
    public event EventHandler<bool>? SaveRequested;

    public void Open(OrganizePagesViewModel viewModel)
    {
        ViewModel = viewModel;
        Bindings.Update();
        SavingPanel.Visibility = Visibility.Collapsed;
        DeleteSelectedButton.IsEnabled = false;
        Visibility = Visibility.Visible;

        // Start at the page the main window was showing.
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            if (viewModel.StartPageIndex < viewModel.Pages.Count)
            {
                PagesGrid.ScrollIntoView(viewModel.Pages[viewModel.StartPageIndex], ScrollIntoViewAlignment.Leading);
            }

            PagesGrid.Focus(FocusState.Programmatic);
        });
    }

    public async Task CloseAsync()
    {
        var viewModel = ViewModel;
        Visibility = Visibility.Collapsed;
        ViewModel = null;
        Bindings.Update();
        _dragged = null;
        if (viewModel is not null)
        {
            await viewModel.CloseAsync();
        }
    }

    public void SetSaving(bool saving) => SavingPanel.Visibility = saving ? Visibility.Visible : Visibility.Collapsed;

    public void Undo() => ViewModel?.Undo();

    public void Redo() => ViewModel?.Redo();

    /// <summary>Whether to open the new file once it is saved (the check box, on by default).</summary>
    public bool OpenAfterSave => OpenAfterSaveCheck.IsChecked == true;

    // ---- buttons ---------------------------------------------------------------------

    private void OnUndoClicked(object sender, RoutedEventArgs e) => ViewModel?.Undo();

    private void OnRedoClicked(object sender, RoutedEventArgs e) => ViewModel?.Redo();

    private void OnResetClicked(object sender, RoutedEventArgs e) => ViewModel?.Reset();

    private void OnDeleteSelectedClicked(object sender, RoutedEventArgs e) => RemoveSelected();

    private void OnCloseClicked(object sender, RoutedEventArgs e) => CloseRequested?.Invoke(this, EventArgs.Empty);

    private void OnSaveClicked(object sender, RoutedEventArgs e) => SaveRequested?.Invoke(this, OpenAfterSave);

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
        => DeleteSelectedButton.IsEnabled = PagesGrid.SelectedItems.Count > 0;

    private void OnGridKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Delete)
        {
            RemoveSelected();
            e.Handled = true;
        }
    }

    private void RemoveSelected()
    {
        if (ViewModel is { } viewModel && PagesGrid.SelectedItems.OfType<OrganizePageItem>().ToList() is { Count: > 0 } selected)
        {
            viewModel.Remove(selected);
        }
    }

    // ---- the grid: pages are dragged to reorder them -----------------------------------

    private void OnDragItemsStarting(object sender, DragItemsStartingEventArgs e)
    {
        _dragged = e.Items.OfType<OrganizePageItem>().ToList();
        e.Data.RequestedOperation = DataPackageOperation.Move;
        e.Data.SetText(string.Join(", ", _dragged.Select(page => page.Position)));
    }

    private void OnDragItemsCompleted(ListViewBase sender, DragItemsCompletedEventArgs args)
    {
        _dragged = null;
        SetTrashActive(false);

        // One undo step for the whole drag (the grid has moved the page by now).
        ViewModel?.Commit();
    }

    /// <summary>Pages on screen get their thumbnail rendered first.</summary>
    private void OnContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.Item is OrganizePageItem item)
        {
            ViewModel?.SetVisible(item, !args.InRecycleQueue);
        }
    }

    // ---- the trash --------------------------------------------------------------------

    private void OnTrashDragOver(object sender, DragEventArgs e)
    {
        if (_dragged is null)
        {
            e.AcceptedOperation = DataPackageOperation.None;
            return;
        }

        e.AcceptedOperation = DataPackageOperation.Move;
        e.DragUIOverride.Caption = _dragged.Count == 1 ? "Elimina la pagina" : $"Elimina {_dragged.Count} pagine";
        e.DragUIOverride.IsGlyphVisible = false;
        SetTrashActive(true);
        e.Handled = true;
    }

    private void OnTrashDragLeave(object sender, DragEventArgs e) => SetTrashActive(false);

    private void OnTrashDrop(object sender, DragEventArgs e)
    {
        SetTrashActive(false);
        if (_dragged is { } pages && ViewModel is { } viewModel)
        {
            viewModel.Remove(pages);
        }

        e.Handled = true;
    }

    /// <summary>The trash grows and turns red while pages are held over it.</summary>
    private void SetTrashActive(bool active)
    {
        TrashZone.Scale = active ? new Vector3(1.15f, 1.15f, 1) : Vector3.One;
        TrashZone.Background = active ? (Brush)Application.Current.Resources["SystemFillColorCriticalBrush"] : _trashBackground;
        TrashIcon.Foreground = active ? new SolidColorBrush(Microsoft.UI.Colors.White) : _trashIconBrush;
        TrashLabel.Foreground = active ? new SolidColorBrush(Microsoft.UI.Colors.White) : _trashIconBrush;
    }
}
