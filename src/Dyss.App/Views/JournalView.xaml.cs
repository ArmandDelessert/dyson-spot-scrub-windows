using Dyss.Presentation.ViewModels;
using Microsoft.UI.Xaml.Controls;

namespace Dyss.App.Views;

/// <summary>The curated log, and the switch that records every message to a daily file.</summary>
public sealed partial class JournalView : UserControl
{
    public JournalView(JournalViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
    }

    public JournalViewModel ViewModel { get; }
}
