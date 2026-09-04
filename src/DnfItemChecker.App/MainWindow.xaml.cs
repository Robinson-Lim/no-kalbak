using System.Windows;
using DnfItemChecker.App.ViewModels;

namespace DnfItemChecker.App;

/// <summary>The shell window hosting the four tabs. Its DataContext is the injected <see cref="MainViewModel"/>.</summary>
public partial class MainWindow : Window
{
    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        Activated += (_, _) => viewModel.SetWindowActive(true);
        Deactivated += (_, _) => viewModel.SetWindowActive(false);
    }
}
