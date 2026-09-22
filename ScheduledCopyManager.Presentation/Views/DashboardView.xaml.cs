namespace ScheduledCopyManager.Presentation.Views
{
    public partial class DashboardView : System.Windows.Controls.UserControl
    {
        public DashboardView()
        {
            InitializeComponent();
        }

        private void OnDataGridRowMouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (sender is System.Windows.Controls.DataGridRow row && row.Item is ScheduledCopyManager.Domain.Models.HistoryEntry entry && DataContext is ViewModels.DashboardViewModel vm)
            {
                _ = vm.ShowHistoryDetailsAsync(entry);
            }
        }
    }
}
