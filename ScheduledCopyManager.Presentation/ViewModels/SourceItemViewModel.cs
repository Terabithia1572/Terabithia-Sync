using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ScheduledCopyManager.Presentation.ViewModels
{
    public partial class SourceItemViewModel : ObservableObject
    {
        [ObservableProperty] private string _path;
        [ObservableProperty] private bool _isDirectory;

        public string Icon => IsDirectory ? "📁" : "📄";
        public string TypeText => IsDirectory ? "Klasör" : "Dosya";

        public SourceItemViewModel(string path)
        {
            _path = path;
            _isDirectory = Directory.Exists(path) || (!File.Exists(path) && !System.IO.Path.HasExtension(path));
        }
    }
}
