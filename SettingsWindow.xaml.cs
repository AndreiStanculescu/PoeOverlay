using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace MyPoeOverlay
{
    public partial class SettingsWindow : Window
    {
        public SettingsWindow()
        {
            InitializeComponent();
        }

        public void SetContent(UIElement content)
        {
            if (content is Border settingsBorder)
            {
                settingsBorder.BorderBrush = System.Windows.Media.Brushes.Transparent;
                settingsBorder.BorderThickness = new Thickness(0);
                settingsBorder.CornerRadius = new CornerRadius(0);
                settingsBorder.Padding = new Thickness(8);
                settingsBorder.Background = System.Windows.Media.Brushes.Transparent;
                settingsBorder.Margin = new Thickness(0);
            }

            SettingsContentHost.Children.Add(content);
        }

        private void HideButton_Click(object sender, RoutedEventArgs e)
        {
            Hide();
        }

        private void Header_MouseLeftButtonDown(
            object sender,
            MouseButtonEventArgs e)
        {
            if (e.ButtonState == MouseButtonState.Pressed)
                DragMove();
        }

        private void ResizeThumb_DragDelta(
            object sender,
            System.Windows.Controls.Primitives.DragDeltaEventArgs e)
        {
            Width = System.Math.Max(MinWidth, Width + e.HorizontalChange);
            Height = System.Math.Max(MinHeight, Height + e.VerticalChange);
        }
    }
}
