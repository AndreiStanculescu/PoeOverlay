
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Diagnostics;
using System.Windows.Threading;


namespace MyPoeOverlay
{
    public partial class MainWindow : Window
    {
        private readonly List<RouteStep> _steps = new();
        private readonly Dictionary<string, string> _areaNames =
            new(StringComparer.OrdinalIgnoreCase);

        private readonly CancellationTokenSource _cancellation = new();

        private readonly Stopwatch _actStopwatch = new();
        private readonly DispatcherTimer _timerUi = new();
        private readonly List<LevelingSession> _sessionHistory = new();

        private LevelingSession? _currentSession;
        private string? _timedActName;

        private TimeSpan _elapsedBeforeStart = TimeSpan.Zero;
        private DateTime _lastTimerSaveUtc = DateTime.MinValue;

        private bool _timerPaused;
        private bool _timerManuallyStopped;

        private bool _isLoadingSettings;

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

            Closed += (_, _) =>
            {
                if (_currentSession != null)
                {
                    SaveCurrentActElapsed();

                    _currentSession.FinishedAt = DateTimeOffset.Now;
                    _currentSession.IsCompleted = false;

                    SaveTimerHistory();
                }

                SaveSettings();

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


        private void SettingsButton_Click(object sender, RoutedEventArgs e)
        {
            SettingsPanel.Visibility =
                SettingsPanel.Visibility == Visibility.Visible
                    ? Visibility.Collapsed
                    : Visibility.Visible;
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


        private void FontSizeSlider_ValueChanged(
            object sender,
            RoutedPropertyChangedEventArgs<double> e)
        {
            if (ProgressText == null ||
                ClientLogStatus == null ||
                FontSizeValueText == null ||
                ObjectiveList == null)
                return;

            double size = FontSizeSlider.Value;

            ProgressText.FontSize = size;
            ClientLogStatus.FontSize = Math.Max(9, size - 3);
            FontSizeValueText.Text = $"{size:0}";

            if (_steps.Count > 0)
                UpdateOverlay();

            SaveSettings();
        }



        private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            try
            {
                LoadSettings();
                LoadTimerHistory();
                UpdateTimerDisplay();

                LoadAreas();
                LoadRoute();

                UpdateOverlay();
                UpdateActTimesDisplay();

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
                ClientLogStatus.Text = "Error: " + ex.Message;
            }
        }

        private void LoadAreas()
        {
            string path = Path.Combine(
                AppContext.BaseDirectory, "Routes", "areas.json");

            using JsonDocument doc = JsonDocument.Parse(
                File.ReadAllText(path));

            foreach (JsonProperty area in doc.RootElement.EnumerateObject())
            {
                if (area.Value.TryGetProperty("name", out JsonElement name))
                {
                    string? areaName = name.GetString();

                    if (!string.IsNullOrWhiteSpace(areaName))
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
                string actName = act.GetProperty("name").GetString()
                                 ?? "Unknown act";

                foreach (JsonElement step in
                         act.GetProperty("steps").EnumerateArray())
                {
                    if (!step.TryGetProperty("parts", out JsonElement parts))
                        continue;

                    string description = RenderParts(parts);
                    string? enterAreaId = null;

                    foreach (JsonElement part in parts.EnumerateArray())
                    {
                        if (part.ValueKind != JsonValueKind.Object)
                            continue;

                        if (GetString(part, "type") == "enter")
                        {
                            enterAreaId = GetString(part, "areaId");
                            break;
                        }
                    }

                    string details = "";

                    if (step.TryGetProperty("subSteps", out JsonElement subSteps)
                        && subSteps.ValueKind == JsonValueKind.Array)
                    {
                        var detailLines = new List<string>();

                        foreach (JsonElement sub in subSteps.EnumerateArray())
                        {
                            if (sub.TryGetProperty("parts", out JsonElement subParts))
                            {
                                string detail = RenderParts(subParts);

                                if (!string.IsNullOrWhiteSpace(detail))
                                    detailLines.Add(detail);
                            }
                        }

                        if (detailLines.Count > 0)
                            details = string.Join(" • ", detailLines);
                    }

                    _steps.Add(new RouteStep(
                        actName, description, enterAreaId, details));
                }
            }

            ActComboBox.ItemsSource = _steps
            .Select(s => s.ActName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (ActComboBox.Items.Count > 0)
            ActComboBox.SelectedIndex = 0;
        }

        private void ActComboBox_SelectionChanged(
            object sender,
            System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (_steps.Count > 0)
                UpdateOverlay();
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
                        result.Append("quest ").Append(
                            GetString(part, "questId") ?? "");
                        break;

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

        private static string? GetString(JsonElement element, string property)
        {
            return element.TryGetProperty(property, out JsonElement value)
                   && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
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
            for (int i = _currentStepIndex; i < _steps.Count; i++)
            {
                string? areaId = _steps[i].EnterAreaId;

                if (areaId == null)
                    continue;

                if (!_areaNames.TryGetValue(areaId, out string? expectedName))
                    continue;

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
                    ActComboBox.SelectedItem =
                        _steps[_currentStepIndex].ActName;
                }

                UpdateOverlay();
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

            string? selectedAct = ActComboBox.SelectedItem as string;

            if (string.IsNullOrWhiteSpace(selectedAct))
                selectedAct = _steps[0].ActName;

            var actSteps = _steps
                .Select((step, index) => new { Step = step, Index = index })
                .Where(x => string.Equals(
                    x.Step.ActName,
                    selectedAct,
                    StringComparison.OrdinalIgnoreCase))
                .ToList();

            int completedCount = actSteps.Count(
                x => x.Index < _currentStepIndex);

            ProgressText.Text = routeComplete
                ? $"{selectedAct} • {actSteps.Count}/{actSteps.Count} tasks complete • Route complete!"
                : $"{selectedAct} • {completedCount}/{actSteps.Count} tasks complete";
            double fontSize = FontSizeSlider.Value;

            var objectives = actSteps.Select(x =>
            {
                bool completed = routeComplete || x.Index < _currentStepIndex;
                bool current = !routeComplete && x.Index == _currentStepIndex;

                string description = x.Step.Description;

                if (!string.IsNullOrWhiteSpace(x.Step.Details))
                    description += "\n" + x.Step.Details;

                return new ObjectiveDisplay(
                    completed ? "✓" : current ? "●" : "○",
                    completed ? Brushes.LightGreen
                        : current ? Brushes.Gold : Brushes.Gray,
                    description,
                    completed ? Brushes.LightGreen
                        : current ? Brushes.White : Brushes.LightGray,
                    fontSize);
            }).ToList();

            ObjectiveList.ItemsSource = objectives;
        }


  
        private void PreviousStepButton_Click(
            object sender, RoutedEventArgs e)
        {
            if (_currentStepIndex <= 0)
                return;

            _currentStepIndex--;

            ActComboBox.SelectedItem = _steps[_currentStepIndex].ActName;

            UpdateOverlay();
        }


        
        private void NextStepButton_Click(
            object sender, RoutedEventArgs e)
        {
            if (_currentStepIndex >= _steps.Count)
                return;

            _currentStepIndex++;

            if (_currentStepIndex < _steps.Count)
            {
                ActComboBox.SelectedItem = _steps[_currentStepIndex].ActName;
            }

            UpdateOverlay();
        }
            

        private void ResizeThumb_DragDelta(
            object sender,
            System.Windows.Controls.Primitives.DragDeltaEventArgs e)
        {
            Width = Math.Max(MinWidth, Width + e.HorizontalChange);
            Height = Math.Max(MinHeight, Height + e.VerticalChange);
        }

        private void CloseButton_Click(
            object sender, RoutedEventArgs e)
        {
            Close();
        }

        private void Overlay_MouseLeftButtonDown(
            object sender, MouseButtonEventArgs e)
        {
            if (e.OriginalSource is System.Windows.Controls.Button)
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

        private void StartNewSession(string actName)
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

            StartActTimer(actName);
        }


        private void StartActTimer(string actName, bool startImmediately = true)
        {
            _timedActName = actName;
            _elapsedBeforeStart = TimeSpan.Zero;
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
            _elapsedBeforeStart = TimeSpan.Zero;
            _timerPaused = false;

            _actStopwatch.Reset();
            _timerUi.Stop();

            TimerStatusText.Text = completed ? "COMPLETED" : "STOPPED";
            TimerActText.Text = completed
                ? "Run completed"
                : "Timer stopped";

            TimerText.Text = "00:00:00";

            SaveTimerHistory();
            UpdateActTimesDisplay();
        }

        private void UpdateTimerDisplay()
        {
            if (TimerText == null ||
                TimerActText == null ||
                TimerStatusText == null)
            {
                return;
            }

            if (_currentSession == null ||
                string.IsNullOrWhiteSpace(_timedActName))
            {
                TimerActText.Text = "Waiting for Act 1";

                if (!_timerManuallyStopped)
                    TimerStatusText.Text = "WAITING";

                TimerText.Text = "00:00:00";
                return;
            }

            TimeSpan elapsed = GetCurrentElapsed();

            TimerActText.Text = _timedActName;
            TimerText.Text = $"{(int)elapsed.TotalHours:00}:" +
                            $"{elapsed.Minutes:00}:" +
                            $"{elapsed.Seconds:00}";

            TimerStatusText.Text = _timerPaused
                ? "PAUSED"
                : "RUNNING";
        }

        private void StartTimerButton_Click(
            object sender, RoutedEventArgs e)
        {
            if (_currentSession != null && _timerPaused)
            {
                _timerPaused = false;
                _actStopwatch.Start();
                _timerUi.Start();
                UpdateTimerDisplay();
                UpdateActTimesDisplay();
                return;
            }

            if (_currentSession != null)
                return;

            // Pornire manuală opțională.
            // În mod normal, timerul pornește automat la intrarea
            // detectată în Actul 1.
            string? selectedAct = ActComboBox.SelectedItem as string;

            if (!string.IsNullOrWhiteSpace(selectedAct))
                StartNewSession(selectedAct);
        }

        private void PauseTimerButton_Click(
            object sender, RoutedEventArgs e)
        {
            if (_currentSession == null || _timerPaused)
                return;

            _elapsedBeforeStart = GetCurrentElapsed();
            _actStopwatch.Reset();
            _timerPaused = true;

            SaveCurrentActElapsed();
            SaveTimerHistory();

            UpdateTimerDisplay();
            UpdateActTimesDisplay();
        }

        private void StopTimerButton_Click(
            object sender, RoutedEventArgs e)
        {
            if (_currentSession == null)
                return;

            _timerManuallyStopped = true;
            FinishSession(completed: false);
        }

        
        private void HandleTimerAreaMatch(
            string matchedActName,
            bool routeComplete)
        {
            string firstActName = _steps[0].ActName;

            if (_currentSession == null)
            {
                if (_timerManuallyStopped)
                    return;

                if (!string.Equals(
                    matchedActName,
                    firstActName,
                    StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                StartNewSession(firstActName);
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
                    _sessionHistory.AddRange(sessions);
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





        private sealed record RouteStep(
            string ActName,
            string Description,
            string? EnterAreaId,
            string Details);


        private sealed record ObjectiveDisplay(
            string Marker,
            Brush MarkerBrush,
            string Description,
            Brush TextBrush,
            double FontSize);



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

            public double WindowWidth { get; set; } = 390;
            public double WindowHeight { get; set; } = 520;

            public double WindowLeft { get; set; } = 100;
            public double WindowTop { get; set; } = 100;
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
                            OpacitySlider.Value =
                                Math.Clamp(settings.OpacityPercent, 35, 100);

                            FontSizeSlider.Value =
                                Math.Clamp(settings.FontSize, 10, 24);

                            Width = Math.Max(MinWidth, settings.WindowWidth);
                            Height = Math.Max(MinHeight, settings.WindowHeight);

                            Left = settings.WindowLeft;
                            Top = settings.WindowTop;
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
                    WindowWidth = Width,
                    WindowHeight = Height,
                    WindowLeft = Left,
                    WindowTop = Top
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
