using Avalonia.Controls;
using SimpleAnalyzer.ViewModels;

namespace SimpleAnalyzer.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        Opened += (_, _) => (DataContext as MainViewModel)?.Start();
        Closed += (_, _) => (DataContext as MainViewModel)?.Stop();
    }
}
