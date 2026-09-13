using Avalonia.Controls;
using Avalonia.Threading;
using LibProsperoPkg.Gui.ViewModels;
using System;
using System.ComponentModel;

namespace LibProsperoPkg.Gui.Views;

public partial class MainWindow : Window
{
    private INotifyPropertyChanged? _viewModel;

    public MainWindow()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        OnDataContextChanged(this, EventArgs.Empty);
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_viewModel is not null)
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        _viewModel = DataContext as INotifyPropertyChanged;
        if (_viewModel is not null)
            _viewModel.PropertyChanged += OnViewModelPropertyChanged;
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == "LogText")
            Dispatcher.UIThread.Post(ScrollLogToEnd, DispatcherPriority.Background);
    }

    private void ScrollLogToEnd()
    {
        if (LogBox is null)
            return;
        // During a build, follow the live footer. After Done the user can scroll
        // and select without the caret jumping back to the end.
        if (DataContext is MainWindowViewModel vm && !vm.IsBusy)
            return;
        string text = LogBox.Text ?? "";
        LogBox.CaretIndex = text.Length;
    }
}
