
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Diagnostics;
using System.Windows.Threading;
using System.Windows.Interop;


namespace MyPoeOverlay
{
    public partial class MainWindow : Window
    {
        private const int MaxAutomaticTaskSkip = 2;
        private const int ClickThroughHotkeyId = 0x4A1;
        private const int WmHotkey = 0x0312;
        private const int GwlExStyle = -20;
        private const long WsExTransparent = 0x00000020L;
        private const uint ModNoRepeat = 0x4000;
        private const uint SwpNoSize = 0x0001;
        private const uint SwpNoMove = 0x0002;
        private const uint SwpNoZOrder = 0x0004;
        private const uint SwpFrameChanged = 0x0020;

        private readonly List<RouteStep> _steps = new();
        private List<ActChoice> _actChoices = new();
        private readonly Dictionary<string, string> _areaNames =
            new(StringComparer.OrdinalIgnoreCase);

        private readonly CancellationTokenSource _cancellation = new();

        private readonly Stopwatch _actStopwatch = new();
        private readonly DispatcherTimer _timerUi = new();
        private readonly List<LevelingSession> _sessionHistory = new();

        private TimerWindow? _timerWindow;
        private SettingsWindow? _settingsWindow;
        private LevelingSession? _currentSession;
        private string? _timedActName;
        private string? _preparedActName;

        private TimeSpan _elapsedBeforeStart = TimeSpan.Zero;
        private TimeSpan _preparedElapsed = TimeSpan.Zero;
        private DateTime _lastTimerSaveUtc = DateTime.MinValue;

        private bool _timerPaused;
        private bool _timerManuallyStopped;
        private bool _timerWaitingForArea;
        private bool _lastSessionCompleted;

        private bool _isLoadingSettings;
        private bool _isUpdatingActSelection;
        private bool _isClosing;
        private bool _isWindowLocked;
        private bool _isClickThroughEnabled;
        private bool _clickThroughHotkeyRegistered;
        private string _clickThroughHotkey = "Ctrl+Shift+O";
        private ModifierKeys _clickThroughModifiers =
            ModifierKeys.Control | ModifierKeys.Shift;
        private Key _clickThroughKey = Key.O;
        private HwndSource? _windowSource;

        private double _timerWindowWidth = 360;
        private double _timerWindowHeight = 280;
        private double _timerWindowLeft = 520;
        private double _timerWindowTop = 100;
        private double _settingsWindowWidth = 500;
        private double _settingsWindowHeight = 620;
        private double _settingsWindowLeft = 100;
        private double _settingsWindowTop = 100;
        private double _actHistoryWindowWidth = 480;
        private double _actHistoryWindowHeight = 360;
        private double _actHistoryWindowLeft = 100;
        private double _actHistoryWindowTop = 100;
        private string SettingsPath => Path.Combine(
            Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData),
            "MyPoeOverlay",
            "settings.json");

        private string TimerHistoryPath =>
            Path.Combine(
                Environment.GetFolderPath(
                    Environment.SpecialFolder.LocalApplicationData),
                "MyPoeOverlay",
                "leveling-times.json");


        private int _currentStepIndex;

        public MainWindow()
        {
            InitializeComponent();

            MouseLeftButtonDown += Overlay_MouseLeftButtonDown;
            Loaded += MainWindow_Loaded;
            SourceInitialized += MainWindow_SourceInitialized;

            Closed += (_, _) =>
            {
                _isClosing = true;

                if (_currentSession != null)
                {
                    SaveCurrentActElapsed();

                    _currentSession.FinishedAt = DateTimeOffset.Now;
                    _currentSession.IsCompleted = false;

                    SaveTimerHistory();
                }

                SaveSettings();
                UnregisterClickThroughHotkey();
                _windowSource?.RemoveHook(MainWindow_WindowMessage);

                _settingsWindow?.Close();
                _timerWindow?.CloseFromMainWindow();
                _timerUi.Stop();
                _cancellation.Cancel();
            };

            _timerUi.Interval = TimeSpan.FromSeconds(1);
            _timerUi.Tick += (_, _) =>
            {
                UpdateTimerDisplay();
                UpdateActTimesDisplay();

                // Salvare periodică pentru a limita pierderea datelor
                // dacă aplicația se închide neașteptat.
                if (DateTime.UtcNow - _lastTimerSaveUtc >=
                    TimeSpan.FromSeconds(10))
                {
                    SaveCurrentActElapsed();
                    SaveTimerHistory();
                }
            };
        }

        private void MainWindow_SourceInitialized(
            object? sender,
            EventArgs e)
        {
            _windowSource = HwndSource.FromHwnd(
                new WindowInteropHelper(this).Handle)
                ?? throw new InvalidOperationException(
                    "Could not access the overlay window handle.");
            _windowSource.AddHook(MainWindow_WindowMessage);
        }

        private IntPtr MainWindow_WindowMessage(
            IntPtr hwnd,
            int message,
            IntPtr wParam,
            IntPtr lParam,
            ref bool handled)
        {
            if (message == WmHotkey &&
                wParam.ToInt32() == ClickThroughHotkeyId)
            {
                ToggleClickThrough();
                handled = true;
            }

            return IntPtr.Zero;
        }

        private void ToggleClickThrough()
        {
            bool previousState = _isClickThroughEnabled;
            bool requestedState = !previousState;

            try
            {
                SetClickThroughEnabled(requestedState);
            }
            catch (Win32Exception ex)
            {
                try
                {
                    SetWindowClickThrough(this, previousState);
                    if (_timerWindow != null)
                        SetWindowClickThrough(_timerWindow, previousState);
                }
                catch (Win32Exception rollbackException)
                {
                    Debug.WriteLine(
                        $"Click-through rollback error: {rollbackException.Message}");
                }

                ClickThroughHotkeyStatusText.Text =
                    $"Could not toggle click-through: {ex.Message}";
                Debug.WriteLine($"Click-through toggle error: {ex.Message}");
            }
        }

        private void SetClickThroughEnabled(bool enabled)
        {
            SetWindowClickThrough(this, enabled);
            if (_timerWindow != null)
                SetWindowClickThrough(_timerWindow, enabled);

            _isClickThroughEnabled = enabled;
            ClickThroughHotkeyStatusText.Text = enabled
                ? $"Click-through enabled • { _clickThroughHotkey } to interact"
                : $"Overlay is interactive • { _clickThroughHotkey } to pass clicks through";
        }

        private static void SetWindowClickThrough(Window window, bool enabled)
        {
            IntPtr hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero)
                return;

            IntPtr currentStyle = GetWindowLongPtr(hwnd, GwlExStyle);
            long style = currentStyle.ToInt64();
            long updatedStyle = enabled
                ? style | WsExTransparent
                : style & ~WsExTransparent;

            if (updatedStyle == style)
                return;

            Marshal.SetLastPInvokeError(0);
            IntPtr previousStyle = SetWindowLongPtr(
                hwnd,
                GwlExStyle,
                new IntPtr(updatedStyle));

            if (previousStyle == IntPtr.Zero &&
                Marshal.GetLastPInvokeError() != 0)
            {
                throw new Win32Exception(
                    Marshal.GetLastPInvokeError(),
                    "Could not change the overlay click-through style.");
            }

            if (!SetWindowPos(
                hwnd,
                IntPtr.Zero,
                0,
                0,
                0,
                0,
                SwpNoMove | SwpNoSize | SwpNoZOrder | SwpFrameChanged))
            {
                throw new Win32Exception(
                    Marshal.GetLastPInvokeError(),
                    "Could not refresh the overlay window style.");
            }
        }

        private void ClickThroughHotkeyTextBox_GotKeyboardFocus(
            object sender,
            KeyboardFocusChangedEventArgs e)
        {
            ClickThroughHotkeyTextBox.Text = "Press shortcut...";
            ClickThroughHotkeyStatusText.Text =
                "Press a key with Ctrl, Alt, Shift, or Windows.";
        }

        private void ClickThroughHotkeyTextBox_LostKeyboardFocus(
            object sender,
            KeyboardFocusChangedEventArgs e)
        {
            ClickThroughHotkeyTextBox.Text = _clickThroughHotkey;
        }

        private void ClickThroughHotkeyTextBox_PreviewKeyDown(
            object sender,
            KeyEventArgs e)
        {
            e.Handled = true;

            Key key = e.Key == Key.System ? e.SystemKey : e.Key;
            if (key is Key.LeftCtrl or Key.RightCtrl or
                Key.LeftAlt or Key.RightAlt or
                Key.LeftShift or Key.RightShift or
                Key.LWin or Key.RWin)
            {
                return;
            }

            ModifierKeys modifiers = Keyboard.Modifiers;
            if (key == Key.Escape)
            {
                ClickThroughHotkeyTextBox.Text = _clickThroughHotkey;
                ClickThroughHotkeyStatusText.Text =
                    "Shortcut change cancelled.";
                Keyboard.ClearFocus();
                return;
            }

            if (modifiers == ModifierKeys.None)
            {
                ClickThroughHotkeyStatusText.Text =
                    "Add Ctrl, Alt, Shift, or Windows to the shortcut.";
                return;
            }

            if (!TrySetClickThroughHotkey(key, modifiers))
            {
                ClickThroughHotkeyTextBox.Text = _clickThroughHotkey;
                return;
            }

            ClickThroughHotkeyTextBox.Text = _clickThroughHotkey;
            ClickThroughHotkeyStatusText.Text =
                $"Shortcut set • {_clickThroughHotkey}";
            SaveSettings();
            Keyboard.ClearFocus();
        }

        private bool TrySetClickThroughHotkey(
            Key key,
            ModifierKeys modifiers)
        {
            if (key == Key.None ||
                key is Key.System or Key.ImeProcessed or
                    Key.DeadCharProcessed or Key.OemClear)
            {
                ClickThroughHotkeyStatusText.Text =
                    "That key cannot be used as a shortcut.";
                return false;
            }

            bool wasRegistered = _clickThroughHotkeyRegistered;
            if (wasRegistered)
                UnregisterClickThroughHotkey();

            if (_windowSource != null &&
                !RegisterHotKey(
                    _windowSource.Handle,
                    ClickThroughHotkeyId,
                    (uint)modifiers | ModNoRepeat,
                    (uint)KeyInterop.VirtualKeyFromKey(key)))
            {
                int error = Marshal.GetLastPInvokeError();
                if (wasRegistered)
                {
                    _clickThroughHotkeyRegistered = RegisterHotKey(
                        _windowSource.Handle,
                        ClickThroughHotkeyId,
                        (uint)_clickThroughModifiers | ModNoRepeat,
                        (uint)KeyInterop.VirtualKeyFromKey(
                            _clickThroughKey));
                }

                ClickThroughHotkeyStatusText.Text =
                    $"Could not register shortcut ({new Win32Exception(error).Message}).";
                return false;
            }

            _clickThroughModifiers = modifiers;
            _clickThroughKey = key;
            _clickThroughHotkey = FormatHotkey(key, modifiers);
            _clickThroughHotkeyRegistered = _windowSource != null;
            return true;
        }

        private static string FormatHotkey(Key key, ModifierKeys modifiers)
        {
            var parts = new List<string>();
            if (modifiers.HasFlag(ModifierKeys.Control))
                parts.Add("Ctrl");
            if (modifiers.HasFlag(ModifierKeys.Alt))
                parts.Add("Alt");
            if (modifiers.HasFlag(ModifierKeys.Shift))
                parts.Add("Shift");
            if (modifiers.HasFlag(ModifierKeys.Windows))
                parts.Add("Win");
            parts.Add(key.ToString());
            return string.Join("+", parts);
        }

        private bool TryLoadClickThroughHotkey(string shortcut)
        {
            string[] parts = shortcut.Split(
                '+',
                StringSplitOptions.RemoveEmptyEntries |
                StringSplitOptions.TrimEntries);
            if (parts.Length < 2 ||
                !Enum.TryParse(parts[^1], true, out Key key))
            {
                return false;
            }

            ModifierKeys modifiers = ModifierKeys.None;
            foreach (string modifier in parts[..^1])
            {
                modifiers |= modifier.ToLowerInvariant() switch
                {
                    "ctrl" or "control" => ModifierKeys.Control,
                    "alt" => ModifierKeys.Alt,
                    "shift" => ModifierKeys.Shift,
                    "win" or "windows" => ModifierKeys.Windows,
                    _ => ModifierKeys.None
                };
            }

            if (modifiers == ModifierKeys.None)
                return false;

            _clickThroughModifiers = modifiers;
            _clickThroughKey = key;
            _clickThroughHotkey = FormatHotkey(key, modifiers);
            return true;
        }

        private void RegisterClickThroughHotkey()
        {
            if (_windowSource == null || _clickThroughHotkeyRegistered)
                return;

            _clickThroughHotkeyRegistered = RegisterHotKey(
                _windowSource.Handle,
                ClickThroughHotkeyId,
                (uint)_clickThroughModifiers | ModNoRepeat,
                (uint)KeyInterop.VirtualKeyFromKey(_clickThroughKey));

            if (!_clickThroughHotkeyRegistered)
            {
                int error = Marshal.GetLastPInvokeError();
                ClickThroughHotkeyStatusText.Text =
                    $"Could not register {_clickThroughHotkey} ({new Win32Exception(error).Message}).";
                return;
            }

            ClickThroughHotkeyTextBox.Text = _clickThroughHotkey;
            ClickThroughHotkeyStatusText.Text =
                $"Overlay is interactive • {_clickThroughHotkey} to pass clicks through";
        }

        private void UnregisterClickThroughHotkey()
        {
            if (!_clickThroughHotkeyRegistered || _windowSource == null)
                return;

            UnregisterHotKey(_windowSource.Handle, ClickThroughHotkeyId);
            _clickThroughHotkeyRegistered = false;
        }

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool RegisterHotKey(
            IntPtr hWnd,
            int id,
            uint fsModifiers,
            uint vk);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
        private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int index);

        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
        private static extern IntPtr SetWindowLongPtr(
            IntPtr hWnd,
            int index,
            IntPtr value);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetWindowPos(
            IntPtr hWnd,
            IntPtr hWndInsertAfter,
            int x,
            int y,
            int cx,
            int cy,
            uint flags);


        private void SettingsButton_Click(object sender, RoutedEventArgs e)
        {
            CreateSettingsWindow();

            if (_settingsWindow == null)
                throw new InvalidOperationException(
                    "The settings window could not be created.");

            if (_settingsWindow.IsVisible)
            {
                _settingsWindow.Hide();
                return;
            }

            _settingsWindow.Show();
            _settingsWindow.Activate();
        }

        private void CreateSettingsWindow()
        {
            if (_settingsWindow != null)
                return;

            if (SettingsPanel.Parent is not System.Windows.Controls.Grid parentGrid)
            {
                throw new InvalidOperationException(
                    "The settings panel is not attached to the overlay.");
            }

            parentGrid.Children.Remove(SettingsPanel);
            SettingsPanel.Visibility = Visibility.Visible;

            _settingsWindow = new SettingsWindow
            {
                Owner = this,
                Width = Math.Max(420, _settingsWindowWidth),
                Height = Math.Max(480, _settingsWindowHeight),
                Left = _settingsWindowLeft,
                Top = _settingsWindowTop
            };
            _settingsWindow.LocationChanged += (_, _) =>
                SaveSettingsWindowGeometry();
            _settingsWindow.SizeChanged += (_, _) =>
                SaveSettingsWindowGeometry();
            _settingsWindow.SetContent(SettingsPanel);
            _settingsWindow.Closing += (_, e) =>
            {
                if (!_isClosing)
                {
                    e.Cancel = true;
                    _settingsWindow.Hide();
                }
            };
        }

        private void ShowTimerWindowButton_Click(
            object sender,
            RoutedEventArgs e)
        {
            CreateTimerWindow();

            if (_timerWindow == null)
                throw new InvalidOperationException(
                    "The timer window could not be created.");

            if (_timerWindow.IsVisible)
            {
                _timerWindow.Hide();
                return;
            }

            _timerWindow.Show();
            _timerWindow.Activate();
        }

        private void CreateTimerWindow()
        {
            if (_timerWindow != null)
                return;

            _timerWindow = new TimerWindow
            {
                Width = Math.Max(340, _timerWindowWidth),
                Height = Math.Max(250, _timerWindowHeight),
                Left = _timerWindowLeft,
                Top = _timerWindowTop,
                Owner = this
            };

            _timerWindow.StartRequested += StartTimerForAct;
            _timerWindow.SetElapsedRequested += SetTimerElapsed;
            _timerWindow.PauseRequested += PauseTimer;
            _timerWindow.StopRequested += StopTimer;
            _timerWindow.FinishActRequested += FinishActManually;
            _timerWindow.ActSelectionChanged += SelectActFromTimer;
            _timerWindow.LocationChanged += (_, _) => SaveTimerWindowSettings();
            _timerWindow.SizeChanged += (_, _) => SaveTimerWindowSettings();
            _timerWindow.Closed += (_, _) =>
            {
                _timerWindow = null;
                SaveTimerWindowSettings();
            };

            ApplyTimerAppearance();
        }

        private void SelectActFromTimer(string actName)
        {
            if (_steps.Count == 0 ||
                string.Equals(
                    ActComboBox.SelectedValue as string,
                    actName,
                    StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            ActComboBox.SelectedValue = actName;
        }

        private void ApplyTimerAppearance()
        {
            _timerWindow?.SetAppearance(
                TimerOpacitySlider.Value / 100.0,
                TimerFontSizeSlider.Value);
        }

        private void SaveTimerWindowSettings()
        {
            if (_timerWindow == null || _isClosing)
                return;

            _timerWindowWidth = _timerWindow.Width;
            _timerWindowHeight = _timerWindow.Height;
            _timerWindowLeft = _timerWindow.Left;
            _timerWindowTop = _timerWindow.Top;
            SaveSettings();
        }

        private void SaveSettingsWindowGeometry()
        {
            if (_settingsWindow == null || _isClosing)
                return;

            _settingsWindowWidth = _settingsWindow.Width;
            _settingsWindowHeight = _settingsWindow.Height;
            _settingsWindowLeft = _settingsWindow.Left;
            _settingsWindowTop = _settingsWindow.Top;
            SaveSettings();
        }

        private void SaveActHistoryWindowGeometry(Window window)
        {
            if (_isClosing)
                return;

            _actHistoryWindowWidth = window.Width;
            _actHistoryWindowHeight = window.Height;
            _actHistoryWindowLeft = window.Left;
            _actHistoryWindowTop = window.Top;
            SaveSettings();
        }

        private void OpacitySlider_ValueChanged(
            object sender,
            RoutedPropertyChangedEventArgs<double> e)
        {
            if (OpacityValueText == null)
                return;

            double opacity = OpacitySlider.Value / 100.0;

            this.Opacity = opacity;
            OpacityValueText.Text = $"{OpacitySlider.Value:0}%";

            SaveSettings();
        }

        private void TimerOpacitySlider_ValueChanged(
            object sender,
            RoutedPropertyChangedEventArgs<double> e)
        {
            if (TimerOpacityValueText == null ||
                TimerOpacitySlider == null)
            {
                return;
            }

            ApplyTimerAppearance();
            TimerOpacityValueText.Text = $"{TimerOpacitySlider.Value:0}%";
            SaveSettings();
        }


        private void FontSizeSlider_ValueChanged(
            object sender,
            RoutedPropertyChangedEventArgs<double> e)
        {
            if (ProgressText == null ||
                ClientLogStatus == null ||
                FontSizeValueText == null ||
                ObjectiveList == null)
                return;

            double size = Math.Round(
                FontSizeSlider.Value,
                MidpointRounding.AwayFromZero);

            if (FontSizeSlider.Value != size)
            {
                FontSizeSlider.Value = size;
                return;
            }

            ProgressText.FontSize = size;
            ClientLogStatus.FontSize = Math.Max(9, size - 3);
            FontSizeValueText.Text = $"{size:0} px";

            if (_steps.Count > 0)
                UpdateOverlay();

            SaveSettings();
        }

        private void TimerFontSizeSlider_ValueChanged(
            object sender,
            RoutedPropertyChangedEventArgs<double> e)
        {
            if (TimerFontSizeValueText == null ||
                TimerFontSizeSlider == null)
            {
                return;
            }

            double size = Math.Round(
                TimerFontSizeSlider.Value,
                MidpointRounding.AwayFromZero);

            if (TimerFontSizeSlider.Value != size)
            {
                TimerFontSizeSlider.Value = size;
                return;
            }

            ApplyTimerAppearance();
            TimerFontSizeValueText.Text = $"{size:0} px";
            SaveSettings();
        }



        private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            try
            {
                LoadSettings();
                RegisterClickThroughHotkey();
                LoadTimerHistory();

                LoadAreas();
                LoadRoute();
                CreateTimerWindow();

                UpdateTimerDisplay();
                UpdateOverlay();
                UpdateActTimesDisplay();

                _timerWindow?.Show();
                if (_isClickThroughEnabled && _timerWindow != null)
                    SetWindowClickThrough(_timerWindow, enabled: true);

                string? logPath = FindClientLog();

                if (logPath == null)
                {
                    ClientLogStatus.Text =
                        "Client.txt not found. Check your PoE installation.";
                    return;
                }

                ClientLogStatus.Text = "Monitoring Client.txt";

                await MonitorClientLogAsync(logPath, _cancellation.Token);
            }
            catch (Exception ex)
            {
                ClientLogStatus.Text = ex.ToString();
            }
        }

       
        private void LoadAreas()
        {
            string path = Path.Combine(
                AppContext.BaseDirectory, "Routes", "areas.json");

            using JsonDocument doc = JsonDocument.Parse(
                File.ReadAllText(path));

            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidDataException(
                    "Routes/areas.json must contain a JSON object.");
            }

            foreach (JsonProperty area in doc.RootElement.EnumerateObject())
            {
                string? areaName = null;

                if (area.Value.ValueKind == JsonValueKind.String)
                {
                    // Format: "area_id": "Area Name"
                    areaName = area.Value.GetString();
                }
                else if (area.Value.ValueKind == JsonValueKind.Object &&
                        area.Value.TryGetProperty("name", out JsonElement name) &&
                        name.ValueKind == JsonValueKind.String)
                {
                    // Format: "area_id": { "name": "Area Name" }
                    areaName = name.GetString();
                }

                if (!string.IsNullOrWhiteSpace(areaName))
                {
                    _areaNames[area.Name] = areaName;
                }
            }
        }


        private void LoadRoute()
        {
            string path = Path.Combine(
                AppContext.BaseDirectory, "Routes", "leveling.json");

            using JsonDocument doc = JsonDocument.Parse(
                File.ReadAllText(path));

            foreach (JsonElement act in doc.RootElement.EnumerateArray())
            {
                if (act.ValueKind != JsonValueKind.Object)
                    continue;

                string actName = act.GetProperty("name").GetString()
                                 ?? "Unknown act";

                JsonElement[] actSteps =
                    act.GetProperty("steps").EnumerateArray().ToArray();

                for (int stepIndex = 0; stepIndex < actSteps.Length; stepIndex++)
                {
                    JsonElement step = actSteps[stepIndex];

                    if (!step.TryGetProperty("parts", out JsonElement parts))
                        continue;

                    string description = RenderParts(parts);
                    string? enterAreaId = GetEnterAreaId(step);
                    string? arenaName = GetArenaName(step);

                    List<string> waypointDestinations =
                        GetWaypointDestinations(step);

                    _steps.Add(new RouteStep(
                        actName,
                        description,
                        enterAreaId,
                        "",
                        arenaName));
                        AddNestedRouteSteps(actName, step);

                    string? logoutTownAreaId = GetLogoutAreaId(step);
                    string? nextStepEnterAreaId =
                        stepIndex + 1 < actSteps.Length
                            ? GetEnterAreaId(actSteps[stepIndex + 1])
                            : null;

                    if (logoutTownAreaId != null &&
                        !string.Equals(
                            logoutTownAreaId,
                            nextStepEnterAreaId,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        string townName =
                            _areaNames.TryGetValue(
                                logoutTownAreaId, out string? areaName)
                                ? areaName
                                : logoutTownAreaId;

                        _steps.Add(new RouteStep(
                            actName,
                            "➞ " + townName,
                            logoutTownAreaId,
                            ""));
                    }

                    foreach (string destination in waypointDestinations)
                    {
                        if (string.Equals(
                            destination,
                            nextStepEnterAreaId,
                            StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        string destinationName =
                            _areaNames.TryGetValue(
                                destination, out string? areaName)
                                ? areaName
                                : destination;

                        _steps.Add(new RouteStep(
                            actName,
                            "➞ " + destinationName,
                            destination,
                            ""));
                    }
                }
            }

            List<string> actNames = _steps
                .Select(step => step.ActName)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            _actChoices = actNames
                .Select(CreateActChoice)
                .ToList();

            ActComboBox.SelectedValuePath = nameof(ActChoice.ActName);
            ActComboBox.ItemsSource = _actChoices;

            if (_actChoices.Count > 0)
                ActComboBox.SelectedValue = _actChoices[0].ActName;
        }

        private ActChoice CreateActChoice(string actName)
        {
            return new ActChoice(actName, actName);
        }

        private void ActComboBox_SelectionChanged(
            object sender,
            System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (_steps.Count == 0 || _isUpdatingActSelection)
                return;

            string? selectedAct = ActComboBox.SelectedValue as string;

            if (string.IsNullOrWhiteSpace(selectedAct))
                return;

            int firstStepIndex = _steps.FindIndex(step =>
                string.Equals(
                    step.ActName,
                    selectedAct,
                    StringComparison.OrdinalIgnoreCase));

            if (firstStepIndex < 0)
                return;

            _currentStepIndex = firstStepIndex;
            PrepareTimerForAct(selectedAct);
            UpdateOverlay();
            ScrollObjectivesToTop();
        }

        private void PrepareTimerForAct(string actName)
        {
            _timerManuallyStopped = false;

            if (_currentSession == null)
            {
                if (!string.Equals(
                    _preparedActName,
                    actName,
                    StringComparison.OrdinalIgnoreCase))
                {
                    _preparedElapsed = TimeSpan.Zero;
                }

                _preparedActName = actName;
                _timedActName = actName;
                _elapsedBeforeStart = _preparedElapsed;
                _actStopwatch.Reset();
                _timerPaused = true;
                _timerWaitingForArea = true;
                _timerUi.Stop();
            }
            else if (!string.Equals(
                _timedActName,
                actName,
                StringComparison.OrdinalIgnoreCase))
            {
                SaveCurrentActElapsed();
                _elapsedBeforeStart = _currentSession.ActTimesSeconds.TryGetValue(
                    actName, out long elapsedSeconds)
                    ? TimeSpan.FromSeconds(elapsedSeconds)
                    : TimeSpan.Zero;
                _timedActName = actName;
                _actStopwatch.Reset();
                _timerPaused = true;
                _timerWaitingForArea = true;
                _timerUi.Stop();
            }

            UpdateTimerDisplay();
            UpdateActTimesDisplay();
        }

        private void SelectActForCurrentStep(string actName)
        {
            _isUpdatingActSelection = true;
            try
            {
                ActComboBox.SelectedValue = actName;
            }
            finally
            {
                _isUpdatingActSelection = false;
            }
        }


        private string RenderParts(JsonElement parts)
        {
            var result = new StringBuilder();

            foreach (JsonElement part in parts.EnumerateArray())
            {
                if (part.ValueKind == JsonValueKind.String)
                {
                    result.Append(part.GetString());
                    continue;
                }

                if (part.ValueKind != JsonValueKind.Object)
                    continue;

                string type = GetString(part, "type") ?? "";

                switch (type)
                {
                    case "arena":
                        result.Append(GetString(part, "value") ?? type);
                        break;

                    case "enter":
                    {
                        string id = GetString(part, "areaId") ?? "";
                        result.Append(_areaNames.TryGetValue(id, out string? name)
                            ? name
                            : id);
                        break;
                    }

                    case "kill":
                    case "quest_text":
                        result.Append(GetString(part, "value") ?? type);
                        break;

                    case "quest":
                        result.Append(
                            GetString(part, "npcName")
                            ?? "quest " + (GetString(part, "questId") ?? ""));
                        break;

                    case "logout":
                        result.Append("Logout");
                        break;

                    case "ascend":
                        result.Append("Ascend");
                        break;

                    case "portal_use":
                        result.Append(GetString(part, "displayText") ?? "Portal");
                        break;

                    case "portal_set":
                        result.Append("portal");
                        break;

                    case "dir":
                    {
                        result.Append(part.GetProperty("dirIndex").GetInt32() switch
                        {
                            0 => "North",
                            1 => "North-East",
                            2 => "East",
                            3 => "South-East",
                            4 => "South",
                            5 => "South-West",
                            6 => "West",
                            7 => "North-West",
                            _ => "direction"
                        });
                        break;
                    }

                    case "waypoint_get":
                        result.Append("waypoint");
                        break;

                    case "waypoint_use":
                    {
                        string destination =
                            GetString(part, "dstAreaId") ?? "";

                        result.Append("Use waypoint to ");
                        result.Append(_areaNames.TryGetValue(
                            destination, out string? name)
                            ? name
                            : destination);
                        break;
                    }

                    default:
                        result.Append(GetString(part, "value") ?? type);
                        break;
                }
            }

            return result.ToString().Trim();
        }

        private void AddNestedRouteSteps(
            string actName,
            JsonElement step)
        {
            if (!step.TryGetProperty("subSteps", out JsonElement subSteps) ||
                subSteps.ValueKind != JsonValueKind.Array)
            {
                return;
            }

            foreach (JsonElement subStep in subSteps.EnumerateArray())
            {
                if (subStep.TryGetProperty("parts", out JsonElement parts) &&
                    parts.ValueKind == JsonValueKind.Array)
                {
                    string? arenaName = GetArenaName(subStep);

                    _steps.Add(new RouteStep(
                        actName,
                        RenderParts(parts),
                        GetEnterAreaId(subStep),
                        "",
                        arenaName));

                        AddNestedRouteSteps(actName, subStep);
                }
                else
                {
                    AddNestedRouteSteps(actName, subStep);
                }
            }
        }

        private static string? GetString(JsonElement element, string property)
        {
            return element.TryGetProperty(property, out JsonElement value)
                   && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
        }

        private static string? GetEnterAreaId(JsonElement step)
        {
            if (!step.TryGetProperty("parts", out JsonElement parts) ||
                parts.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            foreach (JsonElement part in parts.EnumerateArray())
            {
                if (part.ValueKind == JsonValueKind.Object &&
                    GetString(part, "type") == "enter")
                {
                    return GetString(part, "areaId");
                }
            }

            return null;
        }

        private static string? GetArenaName(JsonElement step)
        {
            if (!step.TryGetProperty("parts", out JsonElement parts) ||
                parts.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            foreach (JsonElement part in parts.EnumerateArray())
            {
                if (part.ValueKind == JsonValueKind.Object &&
                    GetString(part, "type") == "arena")
                {
                    string? arenaName = GetString(part, "value");
                    if (!string.IsNullOrWhiteSpace(arenaName))
                        return arenaName;
                }
            }

            return null;
        }

        private static string? GetLogoutAreaId(JsonElement step)
        {
            if (!step.TryGetProperty("parts", out JsonElement parts) ||
                parts.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            foreach (JsonElement part in parts.EnumerateArray())
            {
                if (part.ValueKind == JsonValueKind.Object &&
                    GetString(part, "type") == "logout")
                {
                    return GetString(part, "areaId");
                }
            }

            return null;
        }

        private static List<string> GetWaypointDestinations(JsonElement step)
        {
            var destinations = new List<string>();
            CollectWaypointDestinations(step, destinations);
            return destinations;
        }

        private static void CollectWaypointDestinations(
            JsonElement step, List<string> destinations)
        {
            if (step.TryGetProperty("parts", out JsonElement parts) &&
                parts.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement part in parts.EnumerateArray())
                {
                    if (part.ValueKind != JsonValueKind.Object ||
                        GetString(part, "type") != "waypoint_use")
                    {
                        continue;
                    }

                    string? destination = GetString(part, "dstAreaId");

                    if (!string.IsNullOrWhiteSpace(destination) &&
                        !destinations.Contains(
                            destination,
                            StringComparer.OrdinalIgnoreCase))
                    {
                        destinations.Add(destination);
                    }
                }
            }

            if (step.TryGetProperty("subSteps", out JsonElement subSteps) &&
                subSteps.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement subStep in subSteps.EnumerateArray())
                    CollectWaypointDestinations(subStep, destinations);
            }
        }

        private async Task MonitorClientLogAsync(
            string path, CancellationToken cancellationToken)
        {
            using var stream = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

            // Nu reluăm toate evenimentele istorice de la pornire.
            stream.Seek(0, SeekOrigin.End);

            byte[] bytes = new byte[8192];
            string pending = "";

            while (!cancellationToken.IsCancellationRequested)
            {
                if (stream.Length < stream.Position)
                {
                    // Logul a fost rotit sau recreat.
                    stream.Seek(0, SeekOrigin.Begin);
                    pending = "";
                }

                int read = await stream.ReadAsync(
                    bytes.AsMemory(0, bytes.Length), cancellationToken);

                if (read > 0)
                {
                    pending += Encoding.UTF8.GetString(bytes, 0, read);

                    string[] lines = pending.Split('\n');
                    pending = lines[^1];

                    foreach (string rawLine in lines.Take(lines.Length - 1))
                    {
                        ProcessLogLine(rawLine.TrimEnd('\r'));
                    }
                }

                await Task.Delay(300, cancellationToken);
            }
        }

        private void ProcessLogLine(string line)
        {
            const string marker = "You have entered ";

            int start = line.IndexOf(
                marker, StringComparison.OrdinalIgnoreCase);

            if (start < 0)
                return;

            string areaName = line[(start + marker.Length)..].Trim();

            // Eliminăm punctul final din mesajul Client.txt.
            if (areaName.EndsWith(".", StringComparison.Ordinal))
                areaName = areaName[..^1];

            Dispatcher.Invoke(() =>
            {
                ClientLogStatus.Text = "Detected area: " + areaName;
                AdvanceForArea(areaName);
            });
        }


        private void AdvanceForArea(string enteredAreaName)
        {
            if (_currentStepIndex >= _steps.Count)
                return;

            List<string> actNames = _steps
                .Select(step => step.ActName)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            int currentActOrder = actNames.FindIndex(actName =>
                string.Equals(
                    actName,
                    _steps[_currentStepIndex].ActName,
                    StringComparison.OrdinalIgnoreCase));

            var firstAreaStepByAct = _steps
                .Select((step, index) => new
                {
                    step.ActName,
                    step.IsAreaTask,
                    Index = index
                })
                .Where(step => step.IsAreaTask)
                .GroupBy(step => step.ActName, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    group => group.Key,
                    group => group.First().Index,
                    StringComparer.OrdinalIgnoreCase);

            int currentTaskOrdinal = _steps
                .Take(_currentStepIndex + 1)
                .Count(step => step.IsAreaTask) - 1;
            int candidateTaskOrdinal = _steps
                .Take(_currentStepIndex)
                .Count(step => step.IsAreaTask) - 1;

            for (int i = _currentStepIndex; i < _steps.Count; i++)
            {
                if (_steps[i].IsAreaTask)
                    candidateTaskOrdinal++;

                if (candidateTaskOrdinal - currentTaskOrdinal >
                    MaxAutomaticTaskSkip)
                    break;

                int candidateActOrder = actNames.FindIndex(actName =>
                    string.Equals(
                        actName,
                        _steps[i].ActName,
                        StringComparison.OrdinalIgnoreCase));

                if (candidateActOrder > currentActOrder + 1)
                    continue;

                if (candidateActOrder == currentActOrder + 1 &&
                    (!firstAreaStepByAct.TryGetValue(
                        _steps[i].ActName,
                        out int firstAreaStepIndex) ||
                     i != firstAreaStepIndex))
                {
                    continue;
                }

                string? expectedName = _steps[i].ZoneName;
                string? areaId = _steps[i].EnterAreaId;
                if (expectedName == null &&
                    (areaId == null ||
                     !_areaNames.TryGetValue(areaId, out expectedName)))
                {
                    continue;
                }

                if (string.Equals(
                    expectedName,
                    enteredAreaName,
                    StringComparison.OrdinalIgnoreCase))
                {
                    string matchedActName = _steps[i].ActName;

                    _currentStepIndex = i + 1;

                    bool routeComplete = _currentStepIndex >= _steps.Count;

                    // Pornire și tranziție automată a timerului.
                    HandleTimerAreaMatch(matchedActName, routeComplete);

                    if (_currentStepIndex < _steps.Count)
                    {
                        SelectActForCurrentStep(
                            _steps[_currentStepIndex].ActName);
                    }

                    UpdateOverlay();
                    ScrollObjectivesToTop();
                    return;
                }
            }
        }


        
        private void UpdateOverlay()
        {
            if (_steps.Count == 0)
            {
                ProgressText.Text = "No route steps loaded";
                ObjectiveList.ItemsSource = null;
                return;
            }

            bool routeComplete = _currentStepIndex >= _steps.Count;

            string? selectedAct = ActComboBox.SelectedValue as string;

            if (string.IsNullOrWhiteSpace(selectedAct))
                selectedAct = _steps[0].ActName;

            var actSteps = _steps
                .Select((step, index) => new { Step = step, Index = index })
                .Where(x => string.Equals(
                    x.Step.ActName,
                    selectedAct,
                    StringComparison.OrdinalIgnoreCase))
                .ToList();

            double fontSize = FontSizeSlider.Value;
            double subObjectiveFontSize = Math.Max(10, fontSize - 2);
            var objectiveGroups =
                new List<(int FirstIndex, int MainIndex, string Title, string Details,
                    List<(int Index, RouteStep Step)> SubObjectives)>();
            var leadingSubObjectives =
                new List<(int Index, RouteStep Step)>();
            (int FirstIndex, int MainIndex, string Title, string Details,
                List<(int Index, RouteStep Step)> SubObjectives)? currentGroup = null;

            foreach (var item in actSteps)
            {
                if (item.Step.IsAreaTask)
                {
                    if (currentGroup.HasValue)
                        objectiveGroups.Add(currentGroup.Value);

                    string title = item.Step.ZoneName
                        ?? (_areaNames.TryGetValue(
                                item.Step.EnterAreaId!,
                                out string? areaName)
                            ? areaName
                            : item.Step.Description);

                    currentGroup = (
                        item.Index,
                        item.Index,
                        title,
                        item.Step.Details,
                        leadingSubObjectives);
                    leadingSubObjectives = new();
                    continue;
                }

                if (currentGroup.HasValue)
                {
                    var group = currentGroup.Value;
                    group.SubObjectives.Add((item.Index, item.Step));
                    currentGroup = (
                        group.FirstIndex,
                        item.Index,
                        group.Title,
                        group.Details,
                        group.SubObjectives);
                }
                else
                {
                    leadingSubObjectives.Add((item.Index, item.Step));
                }
            }

            if (currentGroup.HasValue)
                objectiveGroups.Add(currentGroup.Value);

            if (leadingSubObjectives.Count > 0)
            {
                objectiveGroups.Add((
                    leadingSubObjectives[0].Index,
                    leadingSubObjectives[^1].Index,
                    "Act Objectives",
                    "",
                    leadingSubObjectives));
            }

            int completedCount = objectiveGroups.Count(
                group => group.MainIndex < _currentStepIndex);

            ProgressText.Text = routeComplete
                ? $"{selectedAct} • {objectiveGroups.Count}/{objectiveGroups.Count} tasks complete • Route complete!"
                : $"{selectedAct} • {completedCount}/{objectiveGroups.Count} tasks complete";

            int currentGroupIndex = objectiveGroups.FindIndex(group =>
                _currentStepIndex >= group.FirstIndex &&
                _currentStepIndex <= group.MainIndex);

            if (routeComplete)
            {
                currentGroupIndex = objectiveGroups.Count - 1;
            }
            else if (currentGroupIndex < 0)
            {
                currentGroupIndex = objectiveGroups.FindLastIndex(group =>
                    group.MainIndex < _currentStepIndex);
                if (currentGroupIndex < 0)
                    currentGroupIndex = 0;
            }

            const int visibleObjectiveCount = 10;
            const int previousObjectiveCount = 2;
            int startIndex = Math.Max(
                0,
                currentGroupIndex - previousObjectiveCount);

            var objectives = objectiveGroups
                .Skip(startIndex)
                .Take(visibleObjectiveCount)
                .Select(group =>
                {
                    bool completed = routeComplete ||
                        group.MainIndex < _currentStepIndex;
                    bool current = !routeComplete &&
                        _currentStepIndex >= group.FirstIndex &&
                        _currentStepIndex <= group.MainIndex;

                    var subObjectives = group.SubObjectives
                        .Select(item =>
                        {
                            bool subCompleted =
                                routeComplete ||
                                item.Index < _currentStepIndex;
                            bool subCurrent = !routeComplete &&
                                item.Index == _currentStepIndex;
                            string description = item.Step.Description;

                            if (!string.IsNullOrWhiteSpace(item.Step.Details))
                                description += "\n" + item.Step.Details;

                            return new MiniObjectiveDisplay(
                                subCompleted ? "✓" : subCurrent ? "●" : "○",
                                subCompleted ? Brushes.LightGreen
                                    : subCurrent ? Brushes.Gold : Brushes.Gray,
                                description,
                                subCompleted ? Brushes.LightGreen
                                    : subCurrent ? Brushes.White : Brushes.LightGray,
                                subObjectiveFontSize);
                        })
                        .ToList();

                    return new ObjectiveDisplay(
                        completed ? "✓" : current ? "●" : "○",
                        completed ? Brushes.LightGreen
                            : current ? Brushes.Gold : Brushes.Gray,
                        group.Title,
                        completed ? Brushes.LightGreen
                            : current ? Brushes.White : Brushes.LightGray,
                        current ? Brushes.Gold : Brushes.Gray,
                        fontSize,
                        group.Details,
                        subObjectives);
                })
                .ToList();

            ObjectiveList.ItemsSource = objectives;
        }

        private void ScrollObjectivesToTop()
        {
            Dispatcher.BeginInvoke(
                DispatcherPriority.Loaded,
                new Action(ObjectiveScrollViewer.ScrollToTop));
        }


  
        private void PreviousStepButton_Click(
            object sender, RoutedEventArgs e)
        {
            if (_currentStepIndex <= 0)
                return;

            _currentStepIndex--;

            SelectActForCurrentStep(_steps[_currentStepIndex].ActName);

            UpdateOverlay();
            ScrollObjectivesToTop();
        }


        
        private void NextStepButton_Click(
            object sender, RoutedEventArgs e)
        {
            if (_currentStepIndex >= _steps.Count)
                return;

            _currentStepIndex++;

            if (_currentStepIndex < _steps.Count)
            {
                SelectActForCurrentStep(
                    _steps[_currentStepIndex].ActName);
            }
            UpdateOverlay();
            ScrollObjectivesToTop();
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

        private void CloseButton_Click(
            object sender, RoutedEventArgs e)
        {
            Close();
        }

        private void Overlay_MouseLeftButtonDown(
            object sender, MouseButtonEventArgs e)
        {
            if (_isWindowLocked ||
                e.OriginalSource is System.Windows.Controls.Button)
                return;

            DragMove();
        }

        private static string? FindClientLog()
        {
            string programFilesX86 =
                Environment.GetFolderPath(
                    Environment.SpecialFolder.ProgramFilesX86);

            string programFiles =
                Environment.GetFolderPath(
                    Environment.SpecialFolder.ProgramFiles);

            string[] candidates =
            {
                Path.Combine(programFilesX86,
                    "Grinding Gear Games", "Path of Exile",
                    "logs", "Client.txt"),

                Path.Combine(programFiles,
                    "Grinding Gear Games", "Path of Exile",
                    "logs", "Client.txt"),

                Path.Combine(programFilesX86,
                    "Steam", "steamapps", "common",
                    "Path of Exile", "logs", "Client.txt"),

                Path.Combine(programFiles,
                    "Steam", "steamapps", "common",
                    "Path of Exile", "logs", "Client.txt")
            };

            return candidates.FirstOrDefault(File.Exists);
        }


        private TimeSpan GetCurrentElapsed()
        {
            return _elapsedBeforeStart + _actStopwatch.Elapsed;
        }

        private void StartNewSession(
            string actName,
            TimeSpan initialElapsed = default)
        {
            _currentSession = new LevelingSession
            {
                Id = Guid.NewGuid().ToString("N"),
                StartedAt = DateTimeOffset.Now,
                ActTimesSeconds = new Dictionary<string, long>(
                    StringComparer.OrdinalIgnoreCase)
            };

            _sessionHistory.Add(_currentSession);
            _timerManuallyStopped = false;
            _lastSessionCompleted = false;
            _preparedActName = null;
            _preparedElapsed = TimeSpan.Zero;
            _timerWaitingForArea = false;

            StartActTimer(actName, initialElapsed: initialElapsed);
        }


        private void StartActTimer(
            string actName,
            bool startImmediately = true,
            TimeSpan initialElapsed = default)
        {
            _timedActName = actName;
            _elapsedBeforeStart = initialElapsed;
            _timerPaused = !startImmediately;

            _actStopwatch.Reset();

            if (startImmediately)
            {
                _actStopwatch.Start();
                _timerUi.Start();
            }
            else
            {
                _timerUi.Stop();
            }

            UpdateTimerDisplay();
            UpdateActTimesDisplay();
            SaveTimerHistory();
        }


        private void SaveCurrentActElapsed()
        {
            if (_currentSession == null ||
                string.IsNullOrWhiteSpace(_timedActName))
            {
                return;
            }

            _currentSession.ActTimesSeconds[_timedActName] =
                Math.Max(0, (long)GetCurrentElapsed().TotalSeconds);

            _lastTimerSaveUtc = DateTime.UtcNow;
        }

        private void FinishCurrentAct()
        {
            if (_currentSession == null ||
                string.IsNullOrWhiteSpace(_timedActName))
            {
                return;
            }

            SaveCurrentActElapsed();

            _elapsedBeforeStart = GetCurrentElapsed();
            _actStopwatch.Reset();

            _timerUi.Stop();
            _timerPaused = false;

            SaveTimerHistory();
        }

        private void FinishSession(bool completed)
        {
            if (_currentSession == null)
                return;

            FinishCurrentAct();

            _currentSession.IsCompleted = completed;
            _currentSession.FinishedAt = DateTimeOffset.Now;

            _currentSession = null;
            _timedActName = null;
            _preparedActName = null;
            _preparedElapsed = TimeSpan.Zero;
            _elapsedBeforeStart = TimeSpan.Zero;
            _timerPaused = false;
            _timerWaitingForArea = false;

            _actStopwatch.Reset();
            _timerUi.Stop();

            _lastSessionCompleted = completed;

            SaveTimerHistory();
            UpdateTimerDisplay();
            UpdateActTimesDisplay();
        }

        private void UpdateTimerDisplay()
        {
            string actText;
            string elapsedText;
            string statusText;
            string? selectedAct = _timedActName;

            if (_currentSession == null ||
                string.IsNullOrWhiteSpace(_timedActName))
            {
                string waitingAct = _preparedActName
                    ?? _steps.FirstOrDefault()?.ActName
                    ?? "route";
                actText = $"Waiting for {waitingAct}";
                TimeSpan elapsed = _currentSession == null
                    ? _preparedElapsed
                    : _elapsedBeforeStart;
                elapsedText = $"{(long)elapsed.TotalHours:00}:" +
                    $"{elapsed.Minutes:00}:{elapsed.Seconds:00}";

                statusText = _timerManuallyStopped
                    ? "STOPPED"
                    : _lastSessionCompleted
                        ? "COMPLETED"
                        : "WAITING";

                if (_timerManuallyStopped)
                    actText = "Timer stopped";
                else if (_lastSessionCompleted)
                    actText = "Run completed";

                selectedAct ??= _preparedActName
                    ?? ActComboBox.SelectedValue as string
                    ?? _steps.FirstOrDefault()?.ActName;
            }
            else
            {
                TimeSpan elapsed = GetCurrentElapsed();

                actText = _timedActName;
                elapsedText = $"{(int)elapsed.TotalHours:00}:" +
                    $"{elapsed.Minutes:00}:{elapsed.Seconds:00}";
                statusText = _timerPaused || _timerWaitingForArea
                    ? "PAUSED"
                    : "RUNNING";
            }

            _timerWindow?.SetState(
                _actChoices.Select(choice => choice.ActName),
                selectedAct,
                actText,
                elapsedText,
                statusText,
                _currentSession != null);
        }

        private void StartTimerForAct(string selectedAct)
        {
            if (_currentSession != null && _timerPaused)
            {
                _timerPaused = false;
                _timerWaitingForArea = false;
                _actStopwatch.Start();
                _timerUi.Start();
                UpdateTimerDisplay();
                UpdateActTimesDisplay();
                return;
            }

            if (_currentSession != null)
                return;

            if (!string.IsNullOrWhiteSpace(selectedAct))
                StartNewSession(selectedAct, _preparedElapsed);
        }

        private void SetTimerElapsed(TimeSpan elapsed)
        {
            if (_currentSession == null)
            {
                _preparedElapsed = elapsed;
            }
            else
            {
                bool wasRunning = !_timerPaused && !_timerWaitingForArea;

                _elapsedBeforeStart = elapsed;
                _actStopwatch.Reset();

                if (wasRunning)
                    _actStopwatch.Start();

                SaveCurrentActElapsed();
                SaveTimerHistory();
            }

            UpdateTimerDisplay();
            UpdateActTimesDisplay();
        }

        private void PauseTimer()
        {
            if (_currentSession == null || _timerPaused)
                return;

            _elapsedBeforeStart = GetCurrentElapsed();
            _actStopwatch.Reset();
            _timerPaused = true;
            _timerWaitingForArea = false;

            SaveCurrentActElapsed();
            SaveTimerHistory();

            UpdateTimerDisplay();
            UpdateActTimesDisplay();
        }

        private void StopTimer()
        {
            if (_currentSession == null)
                return;

            _timerManuallyStopped = true;
            FinishSession(completed: false);
        }

        private void FinishActManually()
        {
            if (_currentSession == null ||
                string.IsNullOrWhiteSpace(_timedActName))
            {
                return;
            }

            MarkActCompleted(_currentSession, _timedActName);
            FinishCurrentAct();
            _timerPaused = true;
            _timerWaitingForArea = false;

            SaveTimerHistory();
            UpdateTimerDisplay();
            UpdateActTimesDisplay();
        }

        private void ShowActHistoryButton_Click(
            object sender,
            RoutedEventArgs e)
        {
            if (sender is not System.Windows.Controls.Button button ||
                button.Tag is not string actName)
            {
                return;
            }

            var records = new ObservableCollection<ActHistoryRecord>(
                _sessionHistory
                    .Where(session =>
                        session.ActTimesSeconds.ContainsKey(actName))
                    .OrderByDescending(session => session.StartedAt)
                    .Select(session =>
                    {
                        long seconds = session.ActTimesSeconds[actName];
                        bool isCurrentAct =
                            ReferenceEquals(session, _currentSession) &&
                            string.Equals(
                                _timedActName,
                                actName,
                                StringComparison.OrdinalIgnoreCase);

                        if (isCurrentAct)
                        {
                            seconds = (long)GetCurrentElapsed().TotalSeconds;
                        }

                        string status = IsActCompleted(session, actName)
                            ? "COMPLETED"
                            : isCurrentAct
                                ? _timerPaused
                                    ? "PAUSED"
                                    : "IN PROGRESS"
                                : "INCOMPLETE";

                        return new ActHistoryRecord(
                            session.Id,
                            session.StartedAt.ToLocalTime()
                                .ToString("dd MMM yyyy HH:mm"),
                            FormatDuration(seconds),
                            status);
                    }));

            var historyWindow = new ActHistoryWindow(
                actName,
                records,
                sessionId => DeleteActTimeRecord(actName, sessionId))
            {
                Owner = this,
                Width = Math.Max(400, _actHistoryWindowWidth),
                Height = Math.Max(260, _actHistoryWindowHeight),
                Left = _actHistoryWindowLeft,
                Top = _actHistoryWindowTop
            };

            historyWindow.LocationChanged += (_, _) =>
                SaveActHistoryWindowGeometry(historyWindow);
            historyWindow.SizeChanged += (_, _) =>
                SaveActHistoryWindowGeometry(historyWindow);
            historyWindow.Closed += (_, _) =>
                SaveActHistoryWindowGeometry(historyWindow);

            historyWindow.ShowDialog();
        }

        private void DeleteActTimeRecord(string actName, string sessionId)
        {
            LevelingSession? session = _sessionHistory.FirstOrDefault(item =>
                string.Equals(item.Id, sessionId, StringComparison.Ordinal));

            if (session == null)
                return;

            session.ActTimesSeconds.Remove(actName);
            session.CompletedActs.RemoveAll(completedAct =>
                string.Equals(
                    completedAct,
                    actName,
                    StringComparison.OrdinalIgnoreCase));

            if (ReferenceEquals(session, _currentSession) &&
                string.Equals(
                    _timedActName,
                    actName,
                    StringComparison.OrdinalIgnoreCase))
            {
                bool wasRunning = !_timerPaused && !_timerWaitingForArea;
                _elapsedBeforeStart = TimeSpan.Zero;
                _actStopwatch.Reset();

                if (wasRunning)
                    _actStopwatch.Start();

                SaveCurrentActElapsed();
            }

            SaveTimerHistory();
            UpdateTimerDisplay();
            UpdateActTimesDisplay();
        }

        private void ClearActTimesButton_Click(
            object sender, RoutedEventArgs e)
        {
            if (sender is not System.Windows.Controls.Button button ||
                button.Tag is not string actName)
            {
                return;
            }

            foreach (LevelingSession session in _sessionHistory)
            {
                session.ActTimesSeconds.Remove(actName);
                session.CompletedActs.RemoveAll(completedAct =>
                    string.Equals(
                        completedAct,
                        actName,
                        StringComparison.OrdinalIgnoreCase));
            }

            if (_currentSession != null &&
                string.Equals(
                    _timedActName,
                    actName,
                    StringComparison.OrdinalIgnoreCase))
            {
                bool wasRunning = !_timerPaused && !_timerWaitingForArea;
                _elapsedBeforeStart = TimeSpan.Zero;
                _actStopwatch.Reset();

                if (wasRunning)
                    _actStopwatch.Start();
            }

            SaveTimerHistory();
            UpdateTimerDisplay();
            UpdateActTimesDisplay();
        }


        private void HandleTimerAreaMatch(
            string matchedActName,
            bool routeComplete)
        {
            if (_steps.Count == 0)
                return;

            string firstActName = _steps[0].ActName;

            if (_currentSession == null)
            {
                if (_timerManuallyStopped)
                    return;

                string targetActName =
                    _preparedActName ?? firstActName;

                if (!string.Equals(
                    matchedActName,
                    targetActName,
                    StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                StartNewSession(targetActName, _preparedElapsed);
            }
            else if (_timerWaitingForArea &&
                string.Equals(
                    _timedActName,
                    matchedActName,
                    StringComparison.OrdinalIgnoreCase))
            {
                _timerWaitingForArea = false;
                _timerPaused = false;
                _actStopwatch.Start();
                _timerUi.Start();
                UpdateTimerDisplay();
                UpdateActTimesDisplay();
            }
            else if (!string.Equals(
                _timedActName,
                matchedActName,
                StringComparison.OrdinalIgnoreCase))
            {
                bool wasPaused = _timerPaused;

                if (!string.IsNullOrWhiteSpace(_timedActName))
                    MarkActCompleted(_currentSession, _timedActName);

                FinishCurrentAct();
                StartActTimer(matchedActName, startImmediately: !wasPaused);
            }
            if (routeComplete && _currentSession != null &&
                !string.IsNullOrWhiteSpace(_timedActName))
            {
                MarkActCompleted(_currentSession, _timedActName);
                SaveTimerHistory();
            }
            if (routeComplete)
                FinishSession(completed: true);
        }


        private void LoadTimerHistory()
        {
            try
            {
                if (!File.Exists(TimerHistoryPath))
                    return;

                string json = File.ReadAllText(TimerHistoryPath);

                var sessions =
                    JsonSerializer.Deserialize<List<LevelingSession>>(json);

                if (sessions != null)
                {
                    foreach (LevelingSession session in sessions)
                    {
                        if (string.IsNullOrWhiteSpace(session.Id))
                            session.Id = Guid.NewGuid().ToString("N");

                        _sessionHistory.Add(session);
                    }
                }
            }
            catch (Exception ex)
            {
                ClientLogStatus.Text =
                    "Timer history error: " + ex.Message;
            }
        }

        private void SaveTimerHistory()
        {
            try
            {
                string? directory = Path.GetDirectoryName(TimerHistoryPath);

                if (!string.IsNullOrWhiteSpace(directory))
                    Directory.CreateDirectory(directory);

                string json = JsonSerializer.Serialize(
                    _sessionHistory,
                    new JsonSerializerOptions
                    {
                        WriteIndented = true
                    });

                File.WriteAllText(TimerHistoryPath, json);
            }
            catch (Exception ex)
            {
                ClientLogStatus.Text =
                    "Timer save error: " + ex.Message;
            }
        }


        private static string FormatDuration(long seconds)
        {
            TimeSpan time = TimeSpan.FromSeconds(Math.Max(0, seconds));

            return $"{(int)time.TotalHours:00}:" +
                $"{time.Minutes:00}:{time.Seconds:00}";
        }

        private int GetActOrder(string actName)
        {
            for (int i = 0; i < _steps.Count; i++)
            {
                if (string.Equals(
                    _steps[i].ActName,
                    actName,
                    StringComparison.OrdinalIgnoreCase))
                {
                    return i;
                }
            }

            return -1;
        }

        private bool IsActCompleted(
            LevelingSession session,
            string actName)
        {
            if (session.CompletedActs.Contains(
                actName,
                StringComparer.OrdinalIgnoreCase))
            {
                return true;
            }

            // Compatibilitate cu sesiunile vechi, completate integral.
            if (session.IsCompleted &&
                session.ActTimesSeconds.ContainsKey(actName))
            {
                return true;
            }

            // În sesiunile vechi, existența unui timp pentru un act
            // ulterior sugerează că actul anterior a fost terminat.
            if (session.CompletedActs.Count == 0 &&
                session.ActTimesSeconds.ContainsKey(actName))
            {
                int currentOrder = GetActOrder(actName);

                return currentOrder >= 0 &&
                    session.ActTimesSeconds.Keys.Any(name =>
                        GetActOrder(name) > currentOrder);
            }

            return false;
        }


        private void MarkActCompleted(
            LevelingSession? session,
            string actName)
        {
            if (session == null ||
                string.IsNullOrWhiteSpace(actName))
            {
                return;
            }

            if (!session.CompletedActs.Contains(
                actName,
                StringComparer.OrdinalIgnoreCase))
            {
                session.CompletedActs.Add(actName);
            }
        }




        private void UpdateActTimesDisplay()
        {
            if (ActTimesGrid == null || _steps.Count == 0)
                return;

            List<string> acts = _steps
                .Select(step => step.ActName)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            LevelingSession? displaySession = _currentSession
                ?? _sessionHistory
                    .OrderByDescending(session => session.StartedAt)
                    .FirstOrDefault();

            bool isActiveRun = _currentSession != null &&
                            ReferenceEquals(
                                displaySession,
                                _currentSession);

            if (displaySession == null)
            {
                ActTimesSummaryText.Text = "No runs recorded yet";
                ActTimesGrid.ItemsSource = acts.Select(act =>
                    new ActTimeDisplay(
                        act,
                        "—",
                        "—",
                        "NOT STARTED",
                        Brushes.Gray,
                        false)).ToList();

                return;
            }

            int completedCount = acts.Count(act =>
                IsActCompleted(displaySession, act));

            ActTimesSummaryText.Text = isActiveRun
                ? $"Current run • {completedCount}/{acts.Count} acts completed"
                : $"Last run • {displaySession.StartedAt.ToLocalTime():dd MMM HH:mm}";

            var rows = new List<ActTimeDisplay>();

            foreach (string act in acts)
            {
                bool isActiveAct = isActiveRun &&
                    string.Equals(
                        _timedActName,
                        act,
                        StringComparison.OrdinalIgnoreCase);

                bool hasCurrentTime = displaySession.ActTimesSeconds
                    .TryGetValue(act, out long currentSeconds);

                if (isActiveAct)
                {
                    currentSeconds = (long)GetCurrentElapsed().TotalSeconds;
                    hasCurrentTime = true;
                }

                bool completed = IsActCompleted(displaySession, act);

                long? previousBest = _sessionHistory
                    .Where(session =>
                        !ReferenceEquals(session, displaySession) &&
                        IsActCompleted(session, act) &&
                        session.ActTimesSeconds.ContainsKey(act))
                    .Select(session => (long?)session.ActTimesSeconds[act])
                    .Min();

                long? bestEver = _sessionHistory
                    .Where(session =>
                        IsActCompleted(session, act) &&
                        session.ActTimesSeconds.ContainsKey(act))
                    .Select(session => (long?)(
                        isActiveRun &&
                        ReferenceEquals(session, displaySession) &&
                        isActiveAct
                            ? long.MaxValue
                            : session.ActTimesSeconds[act]))
                    .Where(seconds => seconds.HasValue &&
                                    seconds.Value != long.MaxValue)
                    .Min();

                bool isNewPb = completed &&
                            hasCurrentTime &&
                            (!previousBest.HasValue ||
                                currentSeconds < previousBest.Value);

                // Include the just-completed run in the best-ever value.
                if (completed && hasCurrentTime &&
                    (!bestEver.HasValue || currentSeconds < bestEver.Value))
                {
                    bestEver = currentSeconds;
                }

                string status;
                Brush statusBrush;

                if (isNewPb)
                {
                    status = "NEW PB";
                    statusBrush = Brushes.LightGreen;
                }
                else if (isActiveAct)
                {
                    status = _timerPaused ? "PAUSED" : "IN PROGRESS";
                    statusBrush = Brushes.Gold;
                }
                else if (completed)
                {
                    status = "COMPLETED";
                    statusBrush = Brushes.LightGreen;
                }
                else if (hasCurrentTime)
                {
                    status = "INCOMPLETE";
                    statusBrush = Brushes.Gray;
                }
                else
                {
                    status = "NOT STARTED";
                    statusBrush = Brushes.Gray;
                }

                rows.Add(new ActTimeDisplay(
                    act,
                    hasCurrentTime
                        ? FormatDuration(currentSeconds)
                        : "—",
                    bestEver.HasValue
                        ? FormatDuration(bestEver.Value)
                        : "—",
                    status,
                    statusBrush,
                    isNewPb));
            }

            ActTimesGrid.ItemsSource = rows;
        }

        protected override void OnPreviewMouseMove(
            MouseEventArgs e)
        {
            base.OnPreviewMouseMove(e);

            if (e.LeftButton != MouseButtonState.Pressed)
                return;

            if (e.OriginalSource is System.Windows.Controls.Button)
                return;

            if (e.OriginalSource is System.Windows.Controls.Slider)
                return;

            // Permite tragerea ferestrei, nu resize-ul.
            // Resize-ul propriu-zis va fi gestionat de Thumb.
        }



        private sealed record RouteStep(
            string ActName,
            string Description,
            string? EnterAreaId,
            string Details,
            string? ZoneName = null)
        {
            public bool IsAreaTask =>
                EnterAreaId != null || ZoneName != null;
        }


        private sealed record ObjectiveDisplay(
            string Marker,
            Brush MarkerBrush,
            string Title,
            Brush TextBrush,
            Brush BorderBrush,
            double FontSize,
            string Details,
            IReadOnlyList<MiniObjectiveDisplay> SubObjectives);


        private sealed record MiniObjectiveDisplay(
            string Marker,
            Brush MarkerBrush,
            string Description,
            Brush TextBrush,
            double FontSize);

        private sealed record ActChoice(
            string ActName,
            string DisplayName);



        private sealed record ActTimeDisplay(
            string ActName,
            string CurrentRunTime,
            string BestTime,
            string Status,
            Brush StatusBrush,
            bool IsNewPersonalBest);


        private sealed class LevelingSession
        {
            public string Id { get; set; } = "";
            public DateTimeOffset StartedAt { get; set; }
            public DateTimeOffset? FinishedAt { get; set; }
            public bool IsCompleted { get; set; }

            public Dictionary<string, long> ActTimesSeconds { get; set; }
                = new(StringComparer.OrdinalIgnoreCase);

            // Actele terminate efectiv în această sesiune.
            public List<string> CompletedActs { get; set; } = new();
        }

        private sealed class OverlaySettings
        {
            public double OpacityPercent { get; set; } = 100;
            public double FontSize { get; set; } = 16;
            public double? TimerOpacityPercent { get; set; }
            public double? TimerFontSize { get; set; }

            public double WindowWidth { get; set; } = 390;
            public double WindowHeight { get; set; } = 520;

            public double WindowLeft { get; set; } = 100;
            public double WindowTop { get; set; } = 100;

            public double TimerWindowWidth { get; set; } = 360;
            public double TimerWindowHeight { get; set; } = 280;
            public double TimerWindowLeft { get; set; } = 520;
            public double TimerWindowTop { get; set; } = 100;

            public double SettingsWindowWidth { get; set; } = 500;
            public double SettingsWindowHeight { get; set; } = 620;
            public double SettingsWindowLeft { get; set; } = 100;
            public double SettingsWindowTop { get; set; } = 100;

            public double ActHistoryWindowWidth { get; set; } = 480;
            public double ActHistoryWindowHeight { get; set; } = 360;
            public double ActHistoryWindowLeft { get; set; } = 100;
            public double ActHistoryWindowTop { get; set; } = 100;
            public string ClickThroughHotkey { get; set; } = "Ctrl+Shift+O";

        }
        
        private void LoadSettings()
        {
            _isLoadingSettings = true;

            try
            {
                try
                {
                    if (File.Exists(SettingsPath))
                    {
                        string json = File.ReadAllText(SettingsPath);

                        OverlaySettings? settings =
                            JsonSerializer.Deserialize<OverlaySettings>(json);

                        if (settings != null)
                        {
                            if (TryLoadClickThroughHotkey(
                                settings.ClickThroughHotkey))
                            {
                                ClickThroughHotkeyTextBox.Text =
                                    _clickThroughHotkey;
                            }

                            OpacitySlider.Value =
                                Math.Clamp(settings.OpacityPercent, 35, 100);

                            FontSizeSlider.Value =
                                Math.Clamp(
                                    Math.Round(
                                        settings.FontSize,
                                        MidpointRounding.AwayFromZero),
                                    10,
                                    24);

                            TimerOpacitySlider.Value = Math.Clamp(
                                settings.TimerOpacityPercent
                                    ?? settings.OpacityPercent,
                                35,
                                100);

                            TimerFontSizeSlider.Value = Math.Clamp(
                                Math.Round(
                                    settings.TimerFontSize
                                        ?? settings.FontSize,
                                    MidpointRounding.AwayFromZero),
                                10,
                                24);

                            Width = Math.Max(MinWidth, settings.WindowWidth);
                            Height = Math.Max(MinHeight, settings.WindowHeight);

                            Left = settings.WindowLeft;
                            Top = settings.WindowTop;

                            _timerWindowWidth =
                                Math.Max(340, settings.TimerWindowWidth);
                            _timerWindowHeight =
                                Math.Max(250, settings.TimerWindowHeight);
                            _timerWindowLeft = settings.TimerWindowLeft;
                            _timerWindowTop = settings.TimerWindowTop;

                            _settingsWindowWidth =
                                Math.Max(420, settings.SettingsWindowWidth);
                            _settingsWindowHeight =
                                Math.Max(480, settings.SettingsWindowHeight);
                            _settingsWindowLeft = settings.SettingsWindowLeft;
                            _settingsWindowTop = settings.SettingsWindowTop;

                            _actHistoryWindowWidth =
                                Math.Max(400, settings.ActHistoryWindowWidth);
                            _actHistoryWindowHeight =
                                Math.Max(260, settings.ActHistoryWindowHeight);
                            _actHistoryWindowLeft =
                                settings.ActHistoryWindowLeft;
                            _actHistoryWindowTop =
                                settings.ActHistoryWindowTop;

                        }
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Settings load error: {ex.Message}");
                }

                // Actualizează interfața inclusiv dacă fișierul nu există
                // sau dacă nu a putut fi citit.
                OpacitySlider_ValueChanged(
                    OpacitySlider,
                    new RoutedPropertyChangedEventArgs<double>(
                        OpacitySlider.Value,
                        OpacitySlider.Value));

                FontSizeSlider_ValueChanged(
                    FontSizeSlider,
                    new RoutedPropertyChangedEventArgs<double>(
                        FontSizeSlider.Value,
                        FontSizeSlider.Value));

                TimerOpacitySlider_ValueChanged(
                    TimerOpacitySlider,
                    new RoutedPropertyChangedEventArgs<double>(
                        TimerOpacitySlider.Value,
                        TimerOpacitySlider.Value));

                TimerFontSizeSlider_ValueChanged(
                    TimerFontSizeSlider,
                    new RoutedPropertyChangedEventArgs<double>(
                        TimerFontSizeSlider.Value,
                        TimerFontSizeSlider.Value));
            }
            finally
            {
                _isLoadingSettings = false;
            }
        }

        private void SaveSettings()
        {
            if (_isLoadingSettings)
                return;

            try
            {
                string? directory = Path.GetDirectoryName(SettingsPath);

                if (!string.IsNullOrWhiteSpace(directory))
                    Directory.CreateDirectory(directory);

                var settings = new OverlaySettings
                {
                    OpacityPercent = OpacitySlider.Value,
                    FontSize = FontSizeSlider.Value,
                    TimerOpacityPercent = TimerOpacitySlider.Value,
                    TimerFontSize = TimerFontSizeSlider.Value,
                    WindowWidth = Width,
                    WindowHeight = Height,
                    WindowLeft = Left,
                    WindowTop = Top,
                    TimerWindowWidth = _timerWindow?.Width
                        ?? _timerWindowWidth,
                    TimerWindowHeight = _timerWindow?.Height
                        ?? _timerWindowHeight,
                    TimerWindowLeft = _timerWindow?.Left
                        ?? _timerWindowLeft,
                    TimerWindowTop = _timerWindow?.Top
                        ?? _timerWindowTop,
                    SettingsWindowWidth = _settingsWindow?.Width
                        ?? _settingsWindowWidth,
                    SettingsWindowHeight = _settingsWindow?.Height
                        ?? _settingsWindowHeight,
                    SettingsWindowLeft = _settingsWindow?.Left
                        ?? _settingsWindowLeft,
                    SettingsWindowTop = _settingsWindow?.Top
                        ?? _settingsWindowTop,
                    ActHistoryWindowWidth = _actHistoryWindowWidth,
                    ActHistoryWindowHeight = _actHistoryWindowHeight,
                    ActHistoryWindowLeft = _actHistoryWindowLeft,
                    ActHistoryWindowTop = _actHistoryWindowTop,
                    ClickThroughHotkey = _clickThroughHotkey
                };

                string json = JsonSerializer.Serialize(
                    settings,
                    new JsonSerializerOptions { WriteIndented = true });

                File.WriteAllText(SettingsPath, json);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Settings save error: {ex.Message}");
            }
        }

    }
}
