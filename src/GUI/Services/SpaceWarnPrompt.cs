using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using LibProsperoPkg.Gui.Views;

namespace LibProsperoPkg.Gui.Services;

internal static class SpaceWarnPrompt
{
    public static async Task<bool> AskContinueAsync(string title, string body, string continueLabel, string cancel)
    {
        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop
            || desktop.MainWindow is not Window owner)
            return false;

        var window = new SpaceWarnWindow();
        window.SetText(title, body, continueLabel, cancel);
        await window.ShowDialog(owner).ConfigureAwait(true);
        return window.Continue;
    }
}
