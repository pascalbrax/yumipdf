using Microsoft.UI.Xaml;

namespace Pdf26.Services;

/// <summary>
/// Gives a <see cref="Window"/> the app icon (title bar, taskbar button, Alt+Tab). The exe's own
/// icon (<c>ApplicationIcon</c> in App.csproj) only covers Explorer and pinned shortcuts; a
/// running WinUI3 window shows its own icon, which stays the generic default until
/// <c>AppWindow.SetIcon</c> is called. Call this from the constructor of every new
/// <see cref="Window"/> the app opens (ContentDialogs live inside their owner window and need nothing).
/// </summary>
public static class WindowIcon
{
    private const string IconRelativePath = @"Assets\AppIcon.ico";

    public static void Apply(Window window)
    {
        var path = Path.Combine(AppContext.BaseDirectory, IconRelativePath);
        if (!File.Exists(path))
        {
            return;
        }

        window.AppWindow.SetIcon(path);
    }
}
