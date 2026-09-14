using Avalonia.Controls;
using Avalonia.Interactivity;

namespace LibProsperoPkg.Gui.Views;

public partial class SpaceWarnWindow : Window
{
    public bool Continue { get; private set; }

    public SpaceWarnWindow()
    {
        InitializeComponent();
    }

    public void SetText(string title, string body, string continueLabel, string cancelLabel)
    {
        Title = title;
        BodyText.Text = body;
        ContinueButton.Content = continueLabel;
        CancelButton.Content = cancelLabel;
    }

    private void OnContinue(object? sender, RoutedEventArgs e)
    {
        Continue = true;
        Close();
    }

    private void OnCancel(object? sender, RoutedEventArgs e)
    {
        Continue = false;
        Close();
    }
}
