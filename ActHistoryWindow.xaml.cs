using System;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace MyPoeOverlay
{
    public partial class ActHistoryWindow : Window
    {
        private readonly Action<string> _deleteRecord;
        private readonly ObservableCollection<ActHistoryRecord> _records;

        public ActHistoryWindow(
            string actName,
            ObservableCollection<ActHistoryRecord> records,
            Action<string> deleteRecord)
        {
            InitializeComponent();

            _deleteRecord = deleteRecord;
            _records = records;
            TitleText.Text = $"{actName} time records";
            RecordsGrid.ItemsSource = _records;
            UpdateEmptyState();
        }

        private void DeleteRecordButton_Click(
            object sender,
            RoutedEventArgs e)
        {
            if (sender is not Button button ||
                button.Tag is not string sessionId)
            {
                return;
            }

            ActHistoryRecord? record = null;

            foreach (ActHistoryRecord item in _records)
            {
                if (string.Equals(
                    item.SessionId,
                    sessionId,
                    StringComparison.Ordinal))
                {
                    record = item;
                    break;
                }
            }

            if (record == null)
                return;

            _deleteRecord(sessionId);
            _records.Remove(record);
            UpdateEmptyState();
        }

        private void UpdateEmptyState()
        {
            bool isEmpty = _records.Count == 0;
            EmptyText.Visibility = isEmpty
                ? Visibility.Visible
                : Visibility.Collapsed;
            RecordsGrid.Visibility = isEmpty
                ? Visibility.Collapsed
                : Visibility.Visible;
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            Close();
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
            Width = Math.Max(MinWidth, Width + e.HorizontalChange);
            Height = Math.Max(MinHeight, Height + e.VerticalChange);
        }
    }

    public sealed record ActHistoryRecord(
        string SessionId,
        string StartedAt,
        string Duration,
        string Status);
}
