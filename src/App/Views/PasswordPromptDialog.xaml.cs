using Microsoft.UI.Xaml.Controls;

namespace Pdf26.Views;

public sealed partial class PasswordPromptDialog : ContentDialog
{
    public PasswordPromptDialog(bool previousAttemptFailed)
    {
        InitializeComponent();
        if (previousAttemptFailed)
        {
            MessageText.Text = "Password non corretta. Riprova.";
        }
    }

    public string Password => PasswordInput.Password;
}
