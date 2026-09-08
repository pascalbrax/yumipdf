using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using PdfEngine.Forms;

namespace Pdf26.ViewModels;

/// <summary>
/// One AcroForm field, as shown/edited in the "Compila modulo" panel. Wraps the immutable
/// <see cref="PdfFormField"/> snapshot from <c>PdfFormFiller.ListFields()</c> with the
/// editable state the UI binds to, and commits edits back through <see cref="MainViewModel"/>
/// (which owns the <c>PdfFormFiller</c> instance and re-renders the affected page afterwards).
/// </summary>
public partial class FormFieldEntryViewModel : ObservableObject
{
    private readonly MainViewModel _owner;

    // Guards SelectedOptionIndex's change hook from firing (and committing a redundant/premature
    // write back to PDFium) while *we* are the ones assigning it - during construction, and in
    // SyncFromField after a push button's action (e.g. Reset Form) changed it - as opposed to a
    // real user picking a new option in the panel's ComboBox/ListView, which is the only case
    // that should actually commit anything.
    private bool _suppressSelectionCommit;

    /// <param name="options">A ComboBox/ListBox field's selectable labels, in order (from <c>PdfFormFiller.GetOptions</c>) - empty/ignored for every other field type.</param>
    /// <param name="selectedOptionIndices">Which of <paramref name="options"/> are currently selected (from <c>PdfFormFiller.GetSelectedOptionIndices</c>) - 0 or 1 entries for a combo box.</param>
    public FormFieldEntryViewModel(
        PdfFormField field, MainViewModel owner,
        IReadOnlyList<string>? options = null, IReadOnlyList<int>? selectedOptionIndices = null)
    {
        _suppressSelectionCommit = true;
        Field = field;
        _owner = owner;
        Value = field.Value;
        IsChecked = field.IsChecked;
        Options = options ?? [];
        SelectedOptionIndex = selectedOptionIndices is { Count: > 0 } ? selectedOptionIndices[0] : -1;
        _suppressSelectionCommit = false;
    }

    public PdfFormField Field { get; }

    public string Name => Field.Name;

    public int PageIndex => Field.PageIndex;

    public bool IsTextField => Field.Type == PdfFormFieldType.Text;

    /// <summary>Whether Enter should insert a line break instead of doing nothing - the AcroForm "Multiline" flag. Only meaningful when <see cref="IsTextField"/>.</summary>
    public bool IsMultiline => Field.IsMultiline;

    // TextWrapping.NoWrap is the TextBox default already, but exposing it explicitly here
    // (rather than only setting Wrap conditionally in XAML) keeps this alongside IsMultiline -
    // one place per field deciding "is this a multi-line editor" instead of two properties that
    // have to be kept in sync in the XAML by hand.
    public Microsoft.UI.Xaml.TextWrapping OverlayTextWrapping => IsMultiline ? Microsoft.UI.Xaml.TextWrapping.Wrap : Microsoft.UI.Xaml.TextWrapping.NoWrap;

    /// <summary>Centering a single-line value in its (short) overlay box looks right; centering several lines of wrapped text in a tall multi-line box does not - text should start at the top instead.</summary>
    public VerticalAlignment OverlayVerticalContentAlignment => IsMultiline ? VerticalAlignment.Top : VerticalAlignment.Center;

    public bool IsCheckBoxField => Field.Type == PdfFormFieldType.CheckBox;

    public bool IsRadioButtonField => Field.Type == PdfFormFieldType.RadioButton;

    public bool IsCheckableField => IsCheckBoxField || IsRadioButtonField;

    public bool IsComboBoxField => Field.Type == PdfFormFieldType.ComboBox;

    public bool IsListBoxField => Field.Type == PdfFormFieldType.ListBox;

    public bool IsButtonField => Field.Type == PdfFormFieldType.PushButton;

    /// <summary>Signatures: PDFium has no API this project uses to write one, so they stay read-only.</summary>
    public bool IsReadOnlyField => !IsTextField && !IsCheckableField && !IsComboBoxField && !IsListBoxField && !IsButtonField;

    // x:Bind cannot convert bool -> Visibility on its own without a converter, and a
    // StaticResource-based converter is unsafe here (see MainViewModel.ProgressVisibility's
    // remarks on the Window-root x:Bind codegen bug) - exposing Visibility directly sidesteps it.
    public Visibility TextFieldVisibility => IsTextField ? Visibility.Visible : Visibility.Collapsed;

    public Visibility CheckableFieldVisibility => IsCheckableField ? Visibility.Visible : Visibility.Collapsed;

    public Visibility CheckBoxFieldVisibility => IsCheckBoxField ? Visibility.Visible : Visibility.Collapsed;

    public Visibility RadioButtonFieldVisibility => IsRadioButtonField ? Visibility.Visible : Visibility.Collapsed;

    public Visibility ComboBoxFieldVisibility => IsComboBoxField ? Visibility.Visible : Visibility.Collapsed;

    public Visibility ListBoxFieldVisibility => IsListBoxField ? Visibility.Visible : Visibility.Collapsed;

    public Visibility ButtonFieldVisibility => IsButtonField ? Visibility.Visible : Visibility.Collapsed;

    public Visibility ReadOnlyFieldVisibility => IsReadOnlyField ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>A ComboBox/ListBox field's selectable labels, in the PDF's own order - empty for every other field type.</summary>
    public IReadOnlyList<string> Options { get; }

    /// <summary>-1 when nothing is selected. Bound TwoWay to the panel's ComboBox/ListView SelectedIndex.</summary>
    [ObservableProperty]
    public partial int SelectedOptionIndex { get; set; }

    partial void OnSelectedOptionIndexChanged(int value)
    {
        if (_suppressSelectionCommit || value < 0 || value >= Options.Count)
        {
            return;
        }

        if (IsComboBoxField)
        {
            _ = _owner.ApplyComboSelectionAsync(this);
        }
        else if (IsListBoxField)
        {
            _ = _owner.ApplyListSelectionAsync(this);
        }
    }

    /// <summary>Bound to the panel's/overlay's Reset (or other) push button - whatever action the PDF attached to it runs via PDFium's own click handling.</summary>
    public void OnButtonClicked(object sender, RoutedEventArgs e) => _ = _owner.ApplyButtonClickAsync(this);

    // ---- On-page overlay (in-place editing directly on the rendered page) ----------------
    // Position/size in pixels, in the same top-left-origin coordinate space as the page
    // Image, so a caller can lay this out with Canvas.Left/Top - see UpdateOverlay for the
    // PDF-point-to-pixel conversion (PDF points are bottom-left-origin, the opposite of the
    // page bitmap, hence the height flip in there).

    // Position is exposed as a Margin (not Canvas.Left/Top) because the overlay ItemsControl's
    // generated container is a ContentPresenter, not the DataTemplate root itself - Canvas.Left/
    // Top set on the root has no effect on that ContentPresenter's position within the Canvas
    // (confirmed live: every field landed at the same (0,0) spot). Margin is a plain property on
    // the template root, so x:Bind applies it directly with no such indirection.
    [ObservableProperty]
    public partial Thickness OverlayMargin { get; set; }

    [ObservableProperty]
    public partial double OverlayWidth { get; set; } = 1;

    [ObservableProperty]
    public partial double OverlayHeight { get; set; } = 1;

    [ObservableProperty]
    public partial double OverlayFontSize { get; set; } = 12;

    /// <summary>0 for a checkbox's square frame; half the shorter side for a radio button's round one - a radio group is only a valid "pick exactly one" control if it visually reads as a set of round buttons, not squares with a checkmark.</summary>
    [ObservableProperty]
    public partial CornerRadius OverlayCornerRadius { get; set; }

    /// <summary>
    /// Whether the on-page overlay is shown at all - pushed by <see cref="MainViewModel"/>
    /// whenever <see cref="MainViewModel.IsFormPanelOpen"/> changes, rather than each field
    /// subscribing to the owner's PropertyChanged itself, which would leak every field of a
    /// previously-open document (they get recreated on each document open, but the owner -
    /// and any event subscription held by it - lives for the app's whole lifetime).
    /// </summary>
    [ObservableProperty]
    public partial Visibility OverlayVisibility { get; set; } = Visibility.Collapsed;

    /// <summary>Checkbox's own "on" indicator - a checkmark glyph. See <see cref="CheckedDotVisibility"/> for the radio button's.</summary>
    public Visibility CheckedGlyphVisibility => IsChecked && IsCheckBoxField ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Radio button's own "on" indicator - a filled dot, not a checkmark: a radio group only reads as "exactly one of these" when its options look like radio buttons, not checkboxes.</summary>
    public Visibility CheckedDotVisibility => IsChecked && IsRadioButtonField ? Visibility.Visible : Visibility.Collapsed;

    partial void OnIsCheckedChanged(bool value)
    {
        OnPropertyChanged(nameof(CheckedGlyphVisibility));
        OnPropertyChanged(nameof(CheckedDotVisibility));
    }

    /// <summary>Recomputes the overlay's on-screen position/size for a new zoom level - called whenever the owning page is (re)sized.</summary>
    public void UpdateOverlay(double zoom, double pageHeightPoints)
    {
        var left = Field.RectLeft * zoom;
        var top = (pageHeightPoints - Field.RectTop) * zoom;
        OverlayMargin = new Thickness(left, top, 0, 0);
        OverlayWidth = Math.Max(1, (Field.RectRight - Field.RectLeft) * zoom);
        OverlayHeight = Math.Max(1, (Field.RectTop - Field.RectBottom) * zoom);
        OverlayFontSize = Math.Clamp(OverlayHeight * 0.6, 8, 20);
        OverlayCornerRadius = IsRadioButtonField ? new CornerRadius(Math.Min(OverlayWidth, OverlayHeight) / 2) : new CornerRadius(0);
    }

    /// <summary>Tap target for the on-page checkbox/radio overlay (a plain Border, not a WinUI CheckBox - its default chrome/padding does not fit small AcroForm checkbox rectangles well).</summary>
    public void OnOverlayCheckTapped(object sender, TappedRoutedEventArgs e)
    {
        IsChecked = !IsChecked;
        _ = _owner.ApplyFieldCheckedAsync(this);
    }

    public string TypeLabel => Field.Type switch
    {
        PdfFormFieldType.Text => "Testo",
        PdfFormFieldType.CheckBox => "Casella di controllo",
        PdfFormFieldType.RadioButton => "Pulsante di opzione",
        PdfFormFieldType.ComboBox => "Elenco a discesa",
        PdfFormFieldType.ListBox => "Elenco",
        PdfFormFieldType.PushButton => "Pulsante",
        PdfFormFieldType.Signature => "Firma (non modificabile)",
        _ => "Sconosciuto",
    };

    [ObservableProperty]
    public partial string Value { get; set; }

    [ObservableProperty]
    public partial bool IsChecked { get; set; }

    /// <summary>
    /// Keeps <see cref="Value"/> in sync on every keystroke instead of relying on the
    /// TextBox's own TwoWay binding, which in WinUI3 updates the source on the same
    /// LostFocus event <see cref="CommitText"/> is bound to - the ordering between "framework
    /// pushes the edited text into Value" and "our own LostFocus handler runs" is not
    /// guaranteed, and was observed to lose the race (SetFieldValue committing the *old*,
    /// pre-edit Value) when testing this panel live against a real form.
    /// </summary>
    public void OnTextChanged(object sender, TextChangedEventArgs e)
    {
        if (sender is TextBox textBox)
        {
            Value = textBox.Text;
        }
    }

    /// <summary>Bound to the text box's LostFocus in the panel - x:Bind can target a method directly on this item's own DataContext.</summary>
    public void CommitText() => _ = _owner.ApplyFieldTextAsync(this);

    /// <summary>Reads the checkbox's/radio button's own state directly (see <see cref="OnTextChanged"/> for why) rather than trusting a TwoWay-bound <see cref="IsChecked"/> to already be current, then commits.</summary>
    public void OnCheckedChanged(object sender, RoutedEventArgs e)
    {
        IsChecked = sender switch
        {
            CheckBox checkBox => checkBox.IsChecked == true,
            RadioButton radioButton => radioButton.IsChecked == true,
            _ => IsChecked,
        };

        _ = _owner.ApplyFieldCheckedAsync(this);
    }

    /// <summary>
    /// Refreshes this field's editable state from a freshly re-read <see cref="PdfFormField"/> -
    /// used after a push button click (a "Reset Form" action, most commonly), which can change
    /// any field's value, not just the button's own. Never called for a normal user edit of
    /// *this* field: those already know their own new value/state directly.
    /// </summary>
    public void SyncFromField(PdfFormField fresh)
    {
        Value = fresh.Value;
        IsChecked = fresh.IsChecked;

        if (Options.Count > 0)
        {
            var index = -1;
            for (var i = 0; i < Options.Count; i++)
            {
                if (Options[i] == fresh.Value)
                {
                    index = i;
                    break;
                }
            }

            _suppressSelectionCommit = true;
            SelectedOptionIndex = index;
            _suppressSelectionCommit = false;
        }
    }
}
