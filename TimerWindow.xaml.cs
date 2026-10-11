using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace MyPoeOverlay
{
    public partial class TimerWindow : Window
    {
        public event Action<string>? StartRequested;
        public event Action? PauseRequested;
        public event Action? StopRequested;
        public event Action<string>? ActSelectionChanged;
        public event Action<TimeSpan>? SetElapsedRequested;

        private bool _isUpdatingSelection;
        private bool _isEditingElapsed;
        private bool _isWindowLocked;
        private string _latestElapsedText = "00:00:00";

        public string? SelectedActName =>
            ActComboBox.SelectedItem as string;

        public TimerWindow()
        {
            InitializeComponent();
        }

        public void SetState(
            IEnumerable<string> actNames,
            string? selectedAct,
            string actText,
            string elapsedText,
            string statusText)
        {
            string[] names = actNames.ToArray();

            _isUpdatingSelection = true;
            try
            {
                if (!names.SequenceEqual(ActComboBox.Items.Cast<string>()))
                    ActComboBox.ItemsSource = names;

                if (!string.IsNullOrWhiteSpace(selectedAct) &&
                    !string.Equals(
                        SelectedActName,
                        selectedAct,
                        StringComparison.OrdinalIgnoreCase))
                {
                    ActComboBox.SelectedItem = selectedAct;
                }
                else if (ActComboBox.SelectedItem == null && names.Length > 0)
                {
                    ActComboBox.SelectedIndex = 0;
                }
            }
            finally
            {
                _isUpdatingSelection = false;
            }

            TimerActText.Text = actText;
            _latestElapsedText = elapsedText;
            if (!_isEditingElapsed)
                TimerText.Text = elapsedText;
            TimerStatusText.Text = statusText;

            ActComboBox.IsEnabled = true;
        }

        public void SetAppearance(double opacity, double fontSize)
        {
            Opacity = Math.Clamp(opacity, 0.35, 1);

            double scale = Math.Clamp(fontSize / 16.0, 0.625, 1.5);
            TimerActText.FontSize = 11 * scale;
            TimerText.FontSize = 32 * scale;
            TimerStatusText.FontSize = 9 * scale;
        }

        public void CloseFromMainWindow()
        {
            Close();
        }

        private void StartButton_Click(object sender, RoutedEventArgs e)
        {
            string? actName = SelectedActName;

            if (!string.IsNullOrWhiteSpace(actName))
                StartRequested?.Invoke(actName);
        }

        private void PauseButton_Click(object sender, RoutedEventArgs e)
        {
            PauseRequested?.Invoke();
        }

        private void StopButton_Click(object sender, RoutedEventArgs e)
        {
            StopRequested?.Invoke();
        }

        private void TimerText_PreviewMouseLeftButtonDown(
            object sender,
            MouseButtonEventArgs e)
        {
            if (_isEditingElapsed)
                return;

            BeginElapsedEdit();
            e.Handled = true;
        }

        private void BeginElapsedEdit()
        {
            _isEditingElapsed = true;
            TimerText.IsReadOnly = false;
            TimerText.BorderThickness = new Thickness(1);
            TimerText.BorderBrush = new SolidColorBrush(
                Color.FromRgb(0x66, 0x55, 0x22));
            TimerText.ToolTip =
                "Click to edit time. Press Enter to apply or Esc to cancel.";
            TimerText.Text = _latestElapsedText;
            TimerText.Focus();
            TimerText.SelectAll();
        }

        private void TimerText_KeyDown(object sender, KeyEventArgs e)
        {
            if (!_isEditingElapsed)
                return;

            if (e.Key == Key.Enter)
            {
                CommitElapsedEdit();
                e.Handled = true;
                return;
            }

            if (e.Key == Key.Escape)
            {
                CancelElapsedEdit();
                e.Handled = true;
            }
        }

        private void TimerText_LostKeyboardFocus(
            object sender,
            KeyboardFocusChangedEventArgs e)
        {
            if (_isEditingElapsed)
                CommitElapsedEdit();
        }

        private void CommitElapsedEdit()
        {
            if (!TryParseElapsed(TimerText.Text, out TimeSpan elapsed))
            {
                CancelElapsedEdit();
                TimerText.ToolTip = "Invalid time. Use HH:MM:SS.";
                return;
            }

            _isEditingElapsed = false;
            TimerText.IsReadOnly = true;
            TimerText.BorderThickness = new Thickness(0);
            TimerText.ToolTip =
                "Click to edit time. Press Enter to apply or Esc to cancel.";
            TimerText.Text = FormatElapsed(elapsed);
            _latestElapsedText = TimerText.Text;
            SetElapsedRequested?.Invoke(elapsed);
        }

        private void CancelElapsedEdit()
        {
            _isEditingElapsed = false;
            TimerText.IsReadOnly = true;
            TimerText.BorderThickness = new Thickness(0);
            TimerText.Text = _latestElapsedText;
            TimerText.ToolTip =
                "Click to edit time. Press Enter to apply or Esc to cancel.";
        }

        private static bool TryParseElapsed(
            string input,
            out TimeSpan elapsed)
        {
            elapsed = TimeSpan.Zero;
            string[] components = input.Trim().Split(':');

            if (components.Length != 3 ||
                !long.TryParse(
                    components[0],
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out long hours) ||
                !int.TryParse(
                    components[1],
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out int minutes) ||
                !int.TryParse(
                    components[2],
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out int seconds) ||
                minutes is < 0 or > 59 ||
                seconds is < 0 or > 59)
            {
                return false;
            }

            long remainingTicks =
                (long)minutes * TimeSpan.TicksPerMinute +
                (long)seconds * TimeSpan.TicksPerSecond;

            if (hours < 0 ||
                hours >
                    (TimeSpan.MaxValue.Ticks - remainingTicks) /
                    TimeSpan.TicksPerHour)
            {
                return false;
            }

            elapsed = TimeSpan.FromTicks(
                hours * TimeSpan.TicksPerHour + remainingTicks);
            return true;
        }

        private static string FormatElapsed(TimeSpan elapsed)
        {
            return $"{(long)elapsed.TotalHours:00}:" +
                $"{elapsed.Minutes:00}:{elapsed.Seconds:00}";
        }

        private void ActComboBox_SelectionChanged(
            object sender,
            SelectionChangedEventArgs e)
        {
            if (_isUpdatingSelection ||
                string.IsNullOrWhiteSpace(SelectedActName))
            {
                return;
            }

            ActSelectionChanged?.Invoke(SelectedActName);
        }

        private void HideButton_Click(object sender, RoutedEventArgs e)
        {
            Hide();
        }

        private void Header_MouseLeftButtonDown(
            object sender,
            MouseButtonEventArgs e)
        {
            if (!_isWindowLocked &&
                e.ButtonState == MouseButtonState.Pressed &&
                e.OriginalSource is not System.Windows.Controls.Button)
            {
                DragMove();
            }
        }

        private void WindowLockButton_Click(
            object sender,
            RoutedEventArgs e)
        {
            _isWindowLocked = !_isWindowLocked;
            ResizeMode = _isWindowLocked
                ? ResizeMode.NoResize
                : ResizeMode.CanResize;
            ResizeThumb.IsEnabled = !_isWindowLocked;
            ResizeThumb.Cursor = _isWindowLocked
                ? Cursors.Arrow
                : Cursors.SizeNWSE;
            WindowLockButton.Content = _isWindowLocked
                ? "Unlock"
                : "Lock";
            WindowLockButton.ToolTip = _isWindowLocked
                ? "Unlock window position and size"
                : "Lock window position and size";
            WindowLockButton.Background = _isWindowLocked
                ? new SolidColorBrush(Color.FromRgb(58, 53, 36))
                : new SolidColorBrush(Color.FromRgb(41, 41, 41));
        }

        private void ResizeThumb_DragDelta(
            object sender,
            System.Windows.Controls.Primitives.DragDeltaEventArgs e)
        {
            if (_isWindowLocked)
                return;

            Width = Math.Max(MinWidth, Width + e.HorizontalChange);
            Height = Math.Max(MinHeight, Height + e.VerticalChange);
        }
    }
}
