namespace ScheduledCopyManager.Presentation.Views
{
    public partial class LogsView : System.Windows.Controls.UserControl
    {
        public LogsView()
        {
            InitializeComponent();
        }

        private void OnDataGridRowMouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (sender is System.Windows.Controls.DataGridRow row && row.Item is ScheduledCopyManager.Domain.Interfaces.LogEntry entry && DataContext is ViewModels.LogsViewModel vm)
            {
                _ = vm.ShowLogDetailsAsync(entry);
            }
        }
    }
}
