using Avalonia.Controls;
using Avalonia.Interactivity;

namespace HelloWorld.Views
{
    public partial class MainView : UserControl
    {
        public MainView()
        {
            InitializeComponent();
        }

        private void OnExitClick(object? sender, RoutedEventArgs e)
        {
            if (TopLevel.GetTopLevel(this) is Window window)
                window.Close();
        }
    }
}