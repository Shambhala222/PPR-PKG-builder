using Avalonia.Controls;
using Avalonia.Interactivity;

namespace LibProsperoPkg.Gui.Views;

public partial class DiskFullWindow : Window
{
    public bool Retry { get; private set; }

    public DiskFullWindow()
    {
        InitializeComponent();
    }

    public void SetText(
        string title,
        string paused,
        string fileLabel,
        string filePath,
        string hint,
        string cancelHint,
        string retry,
        string cancel)
    {
        Title = title;
        PausedText.Text = paused;
        FileLabel.Text = fileLabel;
        FilePathBox.Text = filePath;
        HintText.Text = hint;
        CancelHintText.Text = cancelHint;
        RetryButton.Content = retry;
        CancelButton.Content = cancel;
    }

    private void OnRetry(object? sender, RoutedEventArgs e)
    {
        Retry = true;
        Close();
    }

    private void OnCancel(object? sender, RoutedEventArgs e)
    {
        Retry = false;
        Close();
    }
}
