using System.Windows;
using System.Windows.Input;

namespace MyPoeOverlay
{
    public partial class MainWindow : Window
    {
        public MainWindow()
        {
            InitializeComponent();

            MouseLeftButtonDown += Overlay_MouseLeftButtonDown;
        }

        private void Overlay_MouseLeftButtonDown(
            object sender,
            MouseButtonEventArgs e)
        {
            // Dacă apăsăm pe butonul X, nu mutăm fereastra
            if (e.OriginalSource is System.Windows.Controls.Button)
                return;

            DragMove();
        }

        private void CloseButton_Click(
            object sender,
            RoutedEventArgs e)
        {
            Close();
        }
    }
}