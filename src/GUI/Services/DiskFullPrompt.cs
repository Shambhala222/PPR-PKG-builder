using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using LibProsperoPkg.Gui.Views;

namespace LibProsperoPkg.Gui.Services;

internal static class DiskFullPrompt
{
    public static bool AskRetry(
        string title,
        string paused,
        string fileLabel,
        string filePath,
        string hint,
        string cancelHint,
        string retry,
        string cancel,
        CancellationToken token)
    {
        if (token.IsCancellationRequested)
            return false;

        var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Dispatcher.UIThread.Post(async () =>
        {
            try
            {
                done.TrySetResult(await ShowAsync(
                    title, paused, fileLabel, filePath, hint, cancelHint, retry, cancel, token)
                    .ConfigureAwait(true));
            }
            catch (Exception ex)
            {
                done.TrySetException(ex);
            }
        });
        return done.Task.GetAwaiter().GetResult();
    }

    private static async Task<bool> ShowAsync(
        string title,
        string paused,
        string fileLabel,
        string filePath,
        string hint,
        string cancelHint,
        string retry,
        string cancel,
        CancellationToken token)
    {
        if (token.IsCancellationRequested)
            return false;

        var window = new DiskFullWindow
        {
            Topmost = false,
            ShowInTaskbar = true,
        };
        window.SetText(title, paused, fileLabel, filePath, hint, cancelHint, retry, cancel);
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        window.Closed += (_, _) => closed.TrySetResult();
        using var _ = token.Register(() =>
            Dispatcher.UIThread.Post(() =>
            {
                if (window.IsVisible)
                    window.Close();
            }));
        window.Show();
        await closed.Task.ConfigureAwait(true);
        return window.Retry && !token.IsCancellationRequested;
    }
}
