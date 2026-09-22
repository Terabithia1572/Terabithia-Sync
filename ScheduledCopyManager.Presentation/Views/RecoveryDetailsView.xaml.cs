using System.Windows;
using ScheduledCopyManager.Presentation.ViewModels;

namespace ScheduledCopyManager.Presentation.Views
{
    public partial class RecoveryDetailsView : Window
    {
        public RecoveryDetailsView(RecoveryDetailsViewModel viewModel)
        {
            InitializeComponent();
            DataContext = viewModel;
            viewModel.RequestClose += () => DialogResult = true;
        }
    }
}
