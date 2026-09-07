using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
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

    public FormFieldEntryViewModel(PdfFormField field, MainViewModel owner)
    {
        Field = field;
        _owner = owner;
        Value = field.Value;
        IsChecked = field.IsChecked;
    }

    public PdfFormField Field { get; }

    public string Name => Field.Name;

    public int PageIndex => Field.PageIndex;

    public bool IsTextField => Field.Type == PdfFormFieldType.Text;

    public bool IsCheckableField => Field.Type is PdfFormFieldType.CheckBox or PdfFormFieldType.RadioButton;

    /// <summary>Push buttons, signatures, combo/list boxes: shown read-only - see docs/ROADMAP.md M2 for what's not covered yet.</summary>
    public bool IsReadOnlyField => !IsTextField && !IsCheckableField;

    // x:Bind cannot convert bool -> Visibility on its own without a converter, and a
    // StaticResource-based converter is unsafe here (see MainViewModel.ProgressVisibility's
    // remarks on the Window-root x:Bind codegen bug) - exposing Visibility directly sidesteps it.
    public Visibility TextFieldVisibility => IsTextField ? Visibility.Visible : Visibility.Collapsed;

    public Visibility CheckableFieldVisibility => IsCheckableField ? Visibility.Visible : Visibility.Collapsed;

    public Visibility ReadOnlyFieldVisibility => IsReadOnlyField ? Visibility.Visible : Visibility.Collapsed;

    public string TypeLabel => Field.Type switch
    {
        PdfFormFieldType.Text => "Testo",
        PdfFormFieldType.CheckBox => "Casella di controllo",
        PdfFormFieldType.RadioButton => "Pulsante di opzione",
        PdfFormFieldType.ComboBox => "Elenco a discesa (non modificabile)",
        PdfFormFieldType.ListBox => "Elenco (non modificabile)",
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

    /// <summary>Reads the checkbox's own state directly (see <see cref="OnTextChanged"/> for why) rather than trusting a TwoWay-bound <see cref="IsChecked"/> to already be current, then commits.</summary>
    public void OnCheckedChanged(object sender, RoutedEventArgs e)
    {
        if (sender is CheckBox checkBox)
        {
            IsChecked = checkBox.IsChecked == true;
        }

        _ = _owner.ApplyFieldCheckedAsync(this);
    }
}
