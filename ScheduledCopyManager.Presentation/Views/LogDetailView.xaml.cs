using System.Windows;
using ScheduledCopyManager.Presentation.ViewModels;

namespace ScheduledCopyManager.Presentation.Views
{
    public partial class LogDetailView : Window
    {
        public LogDetailView()
        {
            InitializeComponent();
        }

        public LogDetailView(LogDetailViewModel vm) : this()
        {
            DataContext = vm;
            vm.RequestClose += Close;
        }
    }
}
