using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using System.Security.Principal;

namespace SpicetifyGuard
{
    internal static class Program
    {
        [STAThread]
        private static int Main(string[] args)
        {
            if (HasArg(args, "--enable-auto"))
                return GuardService.SetAutoRepair(true).Success ? 0 : 4;
            if (HasArg(args, "--disable-auto"))
                return GuardService.SetAutoRepair(false).Success ? 0 : 5;

            bool silentRepair = HasArg(args, "--repair-silent");
            if (silentRepair)
            {
                try
                {
                    GuardStatus status = GuardService.Inspect();
                    if (status.NeedsRepair)
                    {
                        RepairResult result = GuardService.Repair(false);
                        return result.Success ? 0 : 2;
                    }

                    GuardService.Log("Фоновая проверка: патч актуален, действий не требуется.");
                    return 0;
                }
                catch (Exception ex)
                {
                    GuardService.Log("Ошибка фоновой проверки: " + ex);
                    return 3;
                }
            }

            Application app = new Application();
            app.ShutdownMode = ShutdownMode.OnMainWindowClose;
            MainWindow window = new MainWindow();
            app.Run(window);
            return 0;
        }

        private static bool HasArg(string[] args, string value)
        {
            foreach (string arg in args)
            {
                if (string.Equals(arg, value, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }
    }

    internal sealed class GuardStatus
    {
        public string SpicetifyPath;
        public string ConfigPath;
        public string SpotifyPath;
        public string SpotifyVersion;
        public string BackupVersion;
        public string Theme;
        public string Scheme;
        public bool SpotifyRunning;
        public bool NeedsRepair;
        public bool IsReady;
        public string Summary;
    }

    internal sealed class RepairResult
    {
        public bool Success;
        public string Output;
    }

    internal static class GuardService
    {
        private const string TaskName = "SpicetifyGuard Auto Repair";
        private static readonly string DataDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SpicetifyGuard");
        private static readonly string LogPath = Path.Combine(DataDirectory, "guard.log");

        public static GuardStatus Inspect()
        {
            GuardStatus status = new GuardStatus();
            status.SpicetifyPath = FindSpicetify();
            status.ConfigPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "spicetify", "config-xpui.ini");

            Dictionary<string, string> config = ReadIni(status.ConfigPath);
            string spotifyDirectory = GetValue(config, "Setting.spotify_path");
            if (string.IsNullOrWhiteSpace(spotifyDirectory))
                spotifyDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Spotify");

            status.SpotifyPath = Path.Combine(spotifyDirectory, "Spotify.exe");
            status.Theme = GetValue(config, "Setting.current_theme");
            status.Scheme = GetValue(config, "Setting.color_scheme");
            status.BackupVersion = GetValue(config, "Backup.version");
            status.SpotifyVersion = GetProductVersion(status.SpotifyPath);
            status.SpotifyRunning = Process.GetProcessesByName("Spotify").Length > 0;
            status.IsReady = File.Exists(status.SpicetifyPath) && File.Exists(status.ConfigPath) && File.Exists(status.SpotifyPath);

            string currentCore = VersionCore(status.SpotifyVersion);
            string backupCore = VersionCore(status.BackupVersion);
            bool versionMismatch = !string.IsNullOrEmpty(currentCore) && !string.IsNullOrEmpty(backupCore) &&
                                   !string.Equals(currentCore, backupCore, StringComparison.OrdinalIgnoreCase);
            bool themeMissing = string.IsNullOrWhiteSpace(status.Theme) ||
                                string.Equals(status.Theme, "SpicetifyDefault", StringComparison.OrdinalIgnoreCase);
            status.NeedsRepair = status.IsReady && (versionMismatch || themeMissing || string.IsNullOrWhiteSpace(status.BackupVersion));

            if (!File.Exists(status.SpicetifyPath))
                status.Summary = "Spicetify не найден";
            else if (!File.Exists(status.SpotifyPath))
                status.Summary = "Spotify не найден";
            else if (!File.Exists(status.ConfigPath))
                status.Summary = "Конфигурация Spicetify не найдена";
            else if (versionMismatch)
                status.Summary = "Spotify обновился — патч нужно применить заново";
            else if (themeMissing)
                status.Summary = "В конфигурации не выбрана тема";
            else if (string.IsNullOrWhiteSpace(status.BackupVersion))
                status.Summary = "Резервная копия Spicetify ещё не создана";
            else
                status.Summary = "Патч соответствует текущей версии Spotify";

            return status;
        }

        public static RepairResult Repair(bool force)
        {
            using (Mutex mutex = new Mutex(false, "Local\\SpicetifyGuardRepair"))
            {
                bool owns = false;
                try
                {
                    owns = mutex.WaitOne(TimeSpan.FromSeconds(2));
                    if (!owns)
                        return new RepairResult { Success = false, Output = "Другая операция восстановления уже выполняется." };

                    GuardStatus status = Inspect();
                    if (!status.IsReady)
                        return new RepairResult { Success = false, Output = status.Summary };
                    if (!force && !status.NeedsRepair)
                        return new RepairResult { Success = true, Output = "Патч уже актуален." };

                    Log("Начинаю восстановление. Spotify=" + status.SpotifyVersion + ", backup=" + status.BackupVersion + ".");
                    string spicetifyPrefix = "";
                    if (IsElevated())
                    {
                        if (IsUacDisabled())
                        {
                            spicetifyPrefix = "--bypass-admin ";
                            Log("UAC отключён системно; использую обязательный для этой конфигурации флаг --bypass-admin.");
                        }
                        else
                        {
                            return new RepairResult
                            {
                                Success = false,
                                Output = "Spicetify Guard запущен от администратора. Закройте его и запустите обычным двойным кликом."
                            };
                        }
                    }

                    bool wasRunning = StopSpotify();

                    // Keep the patcher compatible with newer Spotify builds. A failed
                    // update is non-fatal: the installed version may still support the
                    // current client and can continue with the repair.
                    RepairResult update = RunProcess(status.SpicetifyPath, spicetifyPrefix + "update", 120000);
                    if (update.Success)
                        Log("Проверено обновление Spicetify.");
                    else
                        Log("Не удалось обновить Spicetify, продолжаю установленной версией. " + LimitText(update.Output, 1000));

                    // `backup apply` refuses to overwrite an existing backup. Restore
                    // the clean app first, then create a fresh backup for the current
                    // Spotify build. This also makes forced repairs idempotent.
                    if (!string.IsNullOrWhiteSpace(status.BackupVersion))
                    {
                        RepairResult restore = RunProcess(status.SpicetifyPath, spicetifyPrefix + "restore backup", 240000);
                        if (!restore.Success)
                        {
                            Log("Не удалось восстановить резервную копию. " + LimitText(restore.Output, 3000));
                            if (wasRunning)
                                StartSpotify(status.SpotifyPath);
                            return restore;
                        }
                        Log("Чистые файлы Spotify восстановлены из резервной копии.");
                    }

                    RepairResult command = RunProcess(status.SpicetifyPath, spicetifyPrefix + "backup apply --no-restart", 240000);

                    if (command.Success)
                    {
                        Log("Spicetify успешно применил патч.");
                        if (wasRunning)
                            StartSpotify(status.SpotifyPath);
                    }
                    else
                    {
                        Log("Spicetify завершился с ошибкой. " + LimitText(command.Output, 3000));
                        if (wasRunning)
                            StartSpotify(status.SpotifyPath);
                    }

                    return command;
                }
                catch (Exception ex)
                {
                    Log("Ошибка восстановления: " + ex);
                    return new RepairResult { Success = false, Output = ex.Message };
                }
                finally
                {
                    if (owns)
                        mutex.ReleaseMutex();
                }
            }
        }

        public static bool IsAutoRepairEnabled()
        {
            RepairResult result = RunProcess("schtasks.exe", "/Query /TN \"" + TaskName + "\"", 15000);
            return result.Success;
        }

        public static RepairResult SetAutoRepair(bool enabled)
        {
            string exe = Process.GetCurrentProcess().MainModule.FileName;
            RepairResult result;
            if (enabled)
            {
                string taskCommand = "\\\"" + exe + "\\\" --repair-silent";
                string args = "/Create /TN \"" + TaskName + "\" /TR \"" + taskCommand + "\" /SC MINUTE /MO 5 /RL LIMITED /F";
                result = RunProcess("schtasks.exe", args, 30000);
                if (result.Success)
                    Log("Включена автоматическая проверка каждые 5 минут.");
            }
            else
            {
                result = RunProcess("schtasks.exe", "/Delete /TN \"" + TaskName + "\" /F", 30000);
                if (result.Success)
                    Log("Автоматическая проверка отключена.");
            }
            return result;
        }

        public static void RestartSpotify()
        {
            GuardStatus status = Inspect();
            StopSpotify();
            StartSpotify(status.SpotifyPath);
            Log("Spotify перезапущен вручную.");
        }

        public static string ReadRecentLog()
        {
            try
            {
                if (!File.Exists(LogPath))
                    return "Лог пока пуст.";
                string contents = File.ReadAllText(LogPath, Encoding.UTF8);
                return LimitText(StripAnsi(contents), 12000);
            }
            catch (Exception ex)
            {
                return "Не удалось прочитать лог: " + ex.Message;
            }
        }

        public static void OpenLogFolder()
        {
            Directory.CreateDirectory(DataDirectory);
            Process.Start("explorer.exe", DataDirectory);
        }

        public static void Log(string message)
        {
            try
            {
                Directory.CreateDirectory(DataDirectory);
                if (File.Exists(LogPath) && new FileInfo(LogPath).Length > 16 * 1024)
                {
                    string compact = LimitText(StripAnsi(File.ReadAllText(LogPath, Encoding.UTF8)), 12000);
                    File.WriteAllText(LogPath, compact, Encoding.UTF8);
                }
                File.AppendAllText(LogPath,
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + message.Replace("\r", " ").Replace("\n", " ") + Environment.NewLine,
                    Encoding.UTF8);
            }
            catch { }
        }

        private static bool StopSpotify()
        {
            Process[] processes = Process.GetProcessesByName("Spotify");
            if (processes.Length == 0)
                return false;

            foreach (Process process in processes)
            {
                try { process.CloseMainWindow(); }
                catch { }
            }

            DateTime deadline = DateTime.UtcNow.AddSeconds(5);
            while (DateTime.UtcNow < deadline && Process.GetProcessesByName("Spotify").Length > 0)
                Thread.Sleep(200);

            foreach (Process process in Process.GetProcessesByName("Spotify"))
            {
                try { process.Kill(); process.WaitForExit(3000); }
                catch { }
            }
            return true;
        }

        private static void StartSpotify(string path)
        {
            try
            {
                if (File.Exists(path))
                    Process.Start(new ProcessStartInfo(path) { WorkingDirectory = Path.GetDirectoryName(path) });
            }
            catch (Exception ex)
            {
                Log("Не удалось перезапустить Spotify: " + ex.Message);
            }
        }

        private static RepairResult RunProcess(string fileName, string arguments, int timeoutMilliseconds)
        {
            try
            {
                ProcessStartInfo start = new ProcessStartInfo(fileName, arguments);
                start.UseShellExecute = false;
                start.CreateNoWindow = true;
                start.WindowStyle = ProcessWindowStyle.Hidden;
                start.RedirectStandardOutput = true;
                start.RedirectStandardError = true;
                start.StandardOutputEncoding = Encoding.UTF8;
                start.StandardErrorEncoding = Encoding.UTF8;

                using (Process process = new Process())
                {
                    process.StartInfo = start;
                    process.Start();
                    Task<string> outputTask = Task.Factory.StartNew(delegate { return process.StandardOutput.ReadToEnd(); });
                    Task<string> errorTask = Task.Factory.StartNew(delegate { return process.StandardError.ReadToEnd(); });
                    if (!process.WaitForExit(timeoutMilliseconds))
                    {
                        try { process.Kill(); } catch { }
                        return new RepairResult { Success = false, Output = "Операция превысила лимит времени." };
                    }
                    Task.WaitAll(new Task[] { outputTask, errorTask }, 5000);
                    string combined = (outputTask.IsCompleted ? outputTask.Result : "") +
                                      (errorTask.IsCompleted ? Environment.NewLine + errorTask.Result : "");
                    return new RepairResult { Success = process.ExitCode == 0, Output = StripAnsi(combined.Trim()) };
                }
            }
            catch (Exception ex)
            {
                return new RepairResult { Success = false, Output = ex.Message };
            }
        }

        private static string FindSpicetify()
        {
            string local = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "spicetify", "spicetify.exe");
            if (File.Exists(local))
                return local;

            string path = Environment.GetEnvironmentVariable("PATH") ?? "";
            foreach (string part in path.Split(Path.PathSeparator))
            {
                try
                {
                    string candidate = Path.Combine(part.Trim(), "spicetify.exe");
                    if (File.Exists(candidate))
                        return candidate;
                }
                catch { }
            }
            return local;
        }

        private static bool IsElevated()
        {
            try
            {
                WindowsIdentity identity = WindowsIdentity.GetCurrent();
                WindowsPrincipal principal = new WindowsPrincipal(identity);
                return principal.IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch { return false; }
        }

        private static bool IsUacDisabled()
        {
            try
            {
                using (RegistryKey key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System"))
                {
                    object value = key == null ? null : key.GetValue("EnableLUA");
                    return value != null && Convert.ToInt32(value) == 0;
                }
            }
            catch { return false; }
        }

        private static Dictionary<string, string> ReadIni(string path)
        {
            Dictionary<string, string> values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (!File.Exists(path))
                return values;

            string section = "";
            foreach (string rawLine in File.ReadAllLines(path))
            {
                string line = rawLine.Trim();
                if (line.Length == 0 || line.StartsWith(";"))
                    continue;
                if (line.StartsWith("[") && line.EndsWith("]"))
                {
                    section = line.Substring(1, line.Length - 2).Trim();
                    continue;
                }
                int equals = line.IndexOf('=');
                if (equals > 0)
                {
                    string key = line.Substring(0, equals).Trim();
                    string value = line.Substring(equals + 1).Trim();
                    values[section + "." + key] = value;
                }
            }
            return values;
        }

        private static string GetValue(Dictionary<string, string> values, string key)
        {
            string value;
            return values.TryGetValue(key, out value) ? value : "";
        }

        private static string GetProductVersion(string path)
        {
            try
            {
                if (!File.Exists(path))
                    return "—";
                FileVersionInfo info = FileVersionInfo.GetVersionInfo(path);
                return string.IsNullOrWhiteSpace(info.ProductVersion) ? info.FileVersion : info.ProductVersion;
            }
            catch { return "—"; }
        }

        private static string VersionCore(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return "";
            Match match = Regex.Match(value, @"\d+\.\d+\.\d+\.\d+");
            return match.Success ? match.Value : value.Trim();
        }

        private static string StripAnsi(string value)
        {
            if (string.IsNullOrEmpty(value))
                return "";
            return Regex.Replace(value, "\\x1B\\[[0-?]*[ -/]*[@-~]", "");
        }

        private static string LimitText(string value, int maxLength)
        {
            if (string.IsNullOrEmpty(value) || value.Length <= maxLength)
                return value ?? "";
            return "…" + value.Substring(value.Length - maxLength);
        }
    }

    internal sealed class MainWindow : Window
    {
        private static readonly Brush WindowBrush = BrushFrom("#101010");
        private static readonly Brush CardBrush = BrushFrom("#1B1B1B");
        private static readonly Brush MutedBrush = BrushFrom("#A7A7A7");
        private static readonly Brush GreenBrush = BrushFrom("#1ED760");
        private static readonly Brush WarningBrush = BrushFrom("#FFB84D");
        private static readonly Brush ErrorBrush = BrushFrom("#FF6B6B");

        private TextBlock statusTitle;
        private TextBlock statusDetail;
        private TextBlock spotifyVersion;
        private TextBlock backupVersion;
        private TextBlock themeValue;
        private TextBlock schemeValue;
        private TextBlock processValue;
        private TextBox logBox;
        private Button repairButton;
        private CheckBox autoRepair;
        private bool initializing;

        public MainWindow()
        {
            Title = "Spicetify Guard";
            Width = 860;
            Height = 690;
            MinWidth = 720;
            MinHeight = 600;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Background = WindowBrush;
            Foreground = Brushes.White;
            FontFamily = new FontFamily("Segoe UI");

            Content = BuildContent();
            Loaded += delegate { RefreshAll(); };
        }

        private UIElement BuildContent()
        {
            Grid root = new Grid();
            root.Margin = new Thickness(32, 24, 32, 28);
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            StackPanel heading = new StackPanel();
            TextBlock title = new TextBlock { Text = "Spicetify Guard", FontSize = 30, FontWeight = FontWeights.SemiBold };
            TextBlock subtitle = new TextBlock
            {
                Text = "Следит за обновлениями Spotify и возвращает тему ZiroNeutral.",
                FontSize = 14,
                Foreground = MutedBrush,
                Margin = new Thickness(0, 6, 0, 0)
            };
            heading.Children.Add(title);
            heading.Children.Add(subtitle);
            Grid.SetRow(heading, 0);
            root.Children.Add(heading);

            Border statusCard = NewCard();
            statusCard.Margin = new Thickness(0, 24, 0, 16);
            Grid statusGrid = new Grid();
            statusGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            statusGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            StackPanel statusText = new StackPanel();
            statusTitle = new TextBlock { FontSize = 20, FontWeight = FontWeights.SemiBold };
            statusDetail = new TextBlock { FontSize = 13, Foreground = MutedBrush, Margin = new Thickness(0, 5, 20, 0), TextWrapping = TextWrapping.Wrap };
            statusText.Children.Add(statusTitle);
            statusText.Children.Add(statusDetail);
            statusGrid.Children.Add(statusText);

            repairButton = NewButton("Починить сейчас", true);
            repairButton.MinWidth = 160;
            repairButton.Click += RepairClick;
            Grid.SetColumn(repairButton, 1);
            statusGrid.Children.Add(repairButton);
            statusCard.Child = statusGrid;
            Grid.SetRow(statusCard, 1);
            root.Children.Add(statusCard);

            Grid middle = new Grid();
            middle.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3, GridUnitType.Star) });
            middle.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
            middle.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star) });

            Border infoCard = NewCard();
            Grid infoGrid = new Grid();
            for (int i = 0; i < 5; i++) infoGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            spotifyVersion = AddInfoRow(infoGrid, 0, "Spotify", "—");
            backupVersion = AddInfoRow(infoGrid, 1, "Резервная копия", "—");
            themeValue = AddInfoRow(infoGrid, 2, "Тема", "—");
            schemeValue = AddInfoRow(infoGrid, 3, "Цветовая схема", "—");
            processValue = AddInfoRow(infoGrid, 4, "Spotify сейчас", "—");
            infoCard.Child = infoGrid;
            middle.Children.Add(infoCard);

            Border controlCard = NewCard();
            Grid controls = new Grid();
            controls.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            controls.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            controls.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            controls.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            TextBlock automationTitle = new TextBlock { Text = "Автовосстановление", FontSize = 16, FontWeight = FontWeights.SemiBold };
            controls.Children.Add(automationTitle);
            TextBlock automationHelp = new TextBlock
            {
                Text = "Тихая проверка каждые 5 минут. Spotify перезапустится только после обнаруженного обновления.",
                Foreground = MutedBrush,
                TextWrapping = TextWrapping.Wrap,
                FontSize = 12,
                Margin = new Thickness(0, 7, 0, 14)
            };
            Grid.SetRow(automationHelp, 1);
            controls.Children.Add(automationHelp);

            autoRepair = new CheckBox { Content = "Включить фоновую защиту", FontSize = 14, VerticalAlignment = VerticalAlignment.Top };
            Grid.SetRow(autoRepair, 2);
            controls.Children.Add(autoRepair);

            Button restartButton = NewButton("Перезапустить Spotify", false);
            restartButton.HorizontalAlignment = HorizontalAlignment.Stretch;
            restartButton.Click += RestartClick;
            Grid.SetRow(restartButton, 3);
            controls.Children.Add(restartButton);
            controlCard.Child = controls;
            Grid.SetColumn(controlCard, 2);
            middle.Children.Add(controlCard);
            Grid.SetRow(middle, 2);
            root.Children.Add(middle);

            Border logCard = NewCard();
            logCard.Margin = new Thickness(0, 16, 0, 0);
            Grid logGrid = new Grid();
            logGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            logGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            Grid logHeader = new Grid();
            logHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            logHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            logHeader.Children.Add(new TextBlock { Text = "Последние события", FontSize = 16, FontWeight = FontWeights.SemiBold });
            Button openLog = NewButton("Открыть папку логов", false);
            openLog.Padding = new Thickness(12, 5, 12, 5);
            openLog.FontSize = 12;
            openLog.Click += delegate { GuardService.OpenLogFolder(); };
            Grid.SetColumn(openLog, 1);
            logHeader.Children.Add(openLog);
            logGrid.Children.Add(logHeader);

            logBox = new TextBox
            {
                Margin = new Thickness(0, 12, 0, 0),
                IsReadOnly = true,
                TextWrapping = TextWrapping.Wrap,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Background = BrushFrom("#141414"),
                Foreground = BrushFrom("#D8D8D8"),
                BorderBrush = BrushFrom("#303030"),
                Padding = new Thickness(10),
                FontFamily = new FontFamily("Consolas"),
                FontSize = 11
            };
            Grid.SetRow(logBox, 1);
            logGrid.Children.Add(logBox);
            logCard.Child = logGrid;
            Grid.SetRow(logCard, 3);
            root.Children.Add(logCard);

            autoRepair.Checked += AutoRepairChanged;
            autoRepair.Unchecked += AutoRepairChanged;
            return root;
        }

        private void RefreshAll()
        {
            initializing = true;
            GuardStatus status = GuardService.Inspect();
            spotifyVersion.Text = EmptyDash(status.SpotifyVersion);
            backupVersion.Text = EmptyDash(status.BackupVersion);
            themeValue.Text = EmptyDash(status.Theme);
            schemeValue.Text = EmptyDash(status.Scheme);
            processValue.Text = status.SpotifyRunning ? "запущен" : "закрыт";
            statusTitle.Text = status.Summary;
            statusDetail.Text = status.NeedsRepair
                ? "Версия резервной копии отличается от установленного Spotify. Нажми кнопку — приложение закроет Spotify, применит backup + apply и откроет его снова."
                : "Можно выполнить принудительное восстановление в любой момент; настройки темы и цветовой схемы сохранятся.";
            statusTitle.Foreground = status.IsReady ? (status.NeedsRepair ? WarningBrush : GreenBrush) : ErrorBrush;
            repairButton.IsEnabled = status.IsReady;
            autoRepair.IsChecked = GuardService.IsAutoRepairEnabled();
            logBox.Text = GuardService.ReadRecentLog();
            logBox.ScrollToEnd();
            initializing = false;
        }

        private async void RepairClick(object sender, RoutedEventArgs e)
        {
            repairButton.IsEnabled = false;
            repairButton.Content = "Исправляю…";
            statusTitle.Text = "Применяю патч Spicetify";
            statusTitle.Foreground = WarningBrush;
            RepairResult result = await Task.Factory.StartNew(delegate { return GuardService.Repair(true); });
            repairButton.Content = "Починить сейчас";
            RefreshAll();
            if (!result.Success)
                MessageBox.Show(this, result.Output, "Spicetify Guard — ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
        }

        private async void AutoRepairChanged(object sender, RoutedEventArgs e)
        {
            if (initializing)
                return;
            bool enabled = autoRepair.IsChecked == true;
            autoRepair.IsEnabled = false;
            RepairResult result = await Task.Factory.StartNew(delegate { return GuardService.SetAutoRepair(enabled); });
            autoRepair.IsEnabled = true;
            if (!result.Success)
            {
                initializing = true;
                autoRepair.IsChecked = !enabled;
                initializing = false;
                MessageBox.Show(this, result.Output, "Не удалось изменить автозапуск", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            logBox.Text = GuardService.ReadRecentLog();
            logBox.ScrollToEnd();
        }

        private async void RestartClick(object sender, RoutedEventArgs e)
        {
            await Task.Factory.StartNew(delegate { GuardService.RestartSpotify(); });
            RefreshAll();
        }

        private static Border NewCard()
        {
            return new Border
            {
                Background = CardBrush,
                BorderBrush = BrushFrom("#2A2A2A"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(20)
            };
        }

        private static Button NewButton(string text, bool primary)
        {
            Button button = new Button
            {
                Content = text,
                Padding = new Thickness(18, 9, 18, 9),
                FontWeight = FontWeights.SemiBold,
                FontSize = 13,
                Cursor = System.Windows.Input.Cursors.Hand
            };
            if (primary)
            {
                button.Background = GreenBrush;
                button.Foreground = Brushes.Black;
                button.BorderBrush = GreenBrush;
            }
            else
            {
                button.Background = BrushFrom("#282828");
                button.Foreground = Brushes.White;
                button.BorderBrush = BrushFrom("#3A3A3A");
            }
            return button;
        }

        private static TextBlock AddInfoRow(Grid grid, int row, string label, string value)
        {
            Grid rowGrid = new Grid { Margin = new Thickness(0, row == 0 ? 0 : 9, 0, 0) };
            rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            TextBlock labelBlock = new TextBlock { Text = label, Foreground = MutedBrush, FontSize = 13 };
            TextBlock valueBlock = new TextBlock { Text = value, FontWeight = FontWeights.SemiBold, FontSize = 13, Margin = new Thickness(18, 0, 0, 0) };
            rowGrid.Children.Add(labelBlock);
            Grid.SetColumn(valueBlock, 1);
            rowGrid.Children.Add(valueBlock);
            Grid.SetRow(rowGrid, row);
            grid.Children.Add(rowGrid);
            return valueBlock;
        }

        private static Brush BrushFrom(string hex)
        {
            return new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        }

        private static string EmptyDash(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? "—" : value;
        }
    }
}
