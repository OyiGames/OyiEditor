using System.Collections.Specialized;
using Avalonia.Controls;
using OyiEditor.App.ViewModels;

namespace OyiEditor.App.Views;

public sealed partial class MainWindow : Window
{
    private MainViewModel? _viewModel;

    public MainWindow()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Attach(DataContext as MainViewModel);
    }

    private void Attach(MainViewModel? viewModel)
    {
        if (_viewModel is not null)
        {
            _viewModel.Log.CollectionChanged -= OnLogChanged;
        }

        _viewModel = viewModel;
        if (_viewModel is not null)
        {
            _viewModel.Log.CollectionChanged += OnLogChanged;
        }
    }

    // Keep the newest console line in view.
    private void OnLogChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Add && _viewModel is { Log.Count: > 0 } viewModel)
        {
            LogList.ScrollIntoView(viewModel.Log.Count - 1);
        }
    }
}
