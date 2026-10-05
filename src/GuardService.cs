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
using System.Linq;
using System.Security.Cryptography;
using System.Web.Script.Serialization;

namespace SpicetifyGuard
{
    internal static class Program
    {
        [STAThread]
        private static int Main(string[] args)
        {
            if (HasArg(args, "--status"))
            {
                Console.WriteLine(new JavaScriptSerializer().Serialize(GuardService.Inspect()));
                return 0;
            }
            if (HasArg(args, "--render-theme") && args.Length == 3)
            {
                try { ThemeStore.Render(ThemeStore.Load(args[1]), args[2]); return 0; }
                catch (Exception ex) { GuardService.Log(ex.Message); return 1; }
            }
            if (HasArg(args, "--repair-now"))
                return GuardService.Repair(true).Success ? 0 : 2;
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
        public bool PatchPresent;
        public bool PaletteMatches;
        public bool CssMatches;
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
            string configPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "spicetify", "config-xpui.ini");
            return InspectAt(configPath, FindSpicetify(), null);
        }

        internal static GuardStatus InspectAt(string configPath, string cliPath, string versionOverride)
        {
            GuardStatus status = new GuardStatus();
            status.SpicetifyPath = cliPath;
            status.ConfigPath = configPath;

            Dictionary<string, string> config = ReadIni(status.ConfigPath);
            string spotifyDirectory = GetValue(config, "Setting.spotify_path");
            if (string.IsNullOrWhiteSpace(spotifyDirectory))
                spotifyDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Spotify");

            status.SpotifyPath = Path.Combine(spotifyDirectory, "Spotify.exe");
            status.Theme = GetValue(config, "Setting.current_theme");
            status.Scheme = GetValue(config, "Setting.color_scheme");
            status.BackupVersion = GetValue(config, "Backup.version");
            status.SpotifyVersion = versionOverride ?? GetProductVersion(status.SpotifyPath);
            status.SpotifyRunning = Process.GetProcessesByName("Spotify").Length > 0;
            status.IsReady = File.Exists(status.SpicetifyPath) && File.Exists(status.ConfigPath) && File.Exists(status.SpotifyPath);

            string currentCore = VersionCore(status.SpotifyVersion);
            string backupCore = VersionCore(status.BackupVersion);
            bool versionMismatch = !string.IsNullOrEmpty(currentCore) && !string.IsNullOrEmpty(backupCore) &&
                                   !string.Equals(currentCore, backupCore, StringComparison.OrdinalIgnoreCase);
            bool themeMissing = string.IsNullOrWhiteSpace(status.Theme) ||
                                string.Equals(status.Theme, "SpicetifyDefault", StringComparison.OrdinalIgnoreCase);
            string xpui = Path.Combine(spotifyDirectory, "Apps", "xpui");
            string themePath = Path.Combine(Path.GetDirectoryName(status.ConfigPath), "Themes", status.Theme);
            status.PatchPresent = File.Exists(Path.Combine(xpui, "helper", "spicetifyWrapper.js")) && File.Exists(Path.Combine(xpui, "spicetify-config.json"));
            status.CssMatches = FilesMatch(Path.Combine(themePath, "user.css"), Path.Combine(xpui, "user.css"));
            status.PaletteMatches = PaletteMatches(status, themePath, xpui);
            status.NeedsRepair = status.IsReady && (versionMismatch || themeMissing || string.IsNullOrWhiteSpace(status.BackupVersion) || !status.PatchPresent || !status.CssMatches || !status.PaletteMatches);

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
            else if (!status.PatchPresent)
                status.Summary = "Патч отсутствует в файлах Spotify";
            else if (!status.CssMatches || !status.PaletteMatches)
                status.Summary = "Тема изменилась — нужно применить настройки";
            else
                status.Summary = "Патч соответствует текущей версии Spotify";

            return status;
        }

        private static bool FilesMatch(string first, string second)
        {
            if (!File.Exists(first) || !File.Exists(second)) return false;
            using (SHA256 sha = SHA256.Create())
                return sha.ComputeHash(File.ReadAllBytes(first)).SequenceEqual(sha.ComputeHash(File.ReadAllBytes(second)));
        }

        private static bool PaletteMatches(GuardStatus status, string themePath, string xpui)
        {
            try
            {
                Dictionary<string, object> injected = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(File.ReadAllText(Path.Combine(xpui, "spicetify-config.json")));
                if ((string)injected["theme_name"] != status.Theme || (string)injected["scheme_name"] != status.Scheme) return false;
                Dictionary<string, object> schemes = (Dictionary<string, object>)injected["schemes"];
                Dictionary<string, object> colors = (Dictionary<string, object>)schemes[status.Scheme];
                Dictionary<string, string> expected = ReadIni(Path.Combine(themePath, "color.ini"));
                foreach (string key in ThemeStore.ColorKeys)
                {
                    string value = GetValue(expected, status.Scheme + "." + key);
                    object actual;
                    if (!string.IsNullOrEmpty(value) && (!colors.TryGetValue(key, out actual) || !string.Equals(value, Convert.ToString(actual), StringComparison.OrdinalIgnoreCase))) return false;
                }
                return true;
            }
            catch { return false; }
        }

        public static RepairResult ApplyTheme(ThemeDefinition theme)
        {
            // Repair owns the same mutex, so theme files cannot change mid-repair.
            using (Mutex mutex = new Mutex(false, "Local\\SpicetifyGuardRepair"))
            {
                bool owns = false;
                try
                {
                    owns = mutex.WaitOne(TimeSpan.FromSeconds(2));
                    if (!owns) return new RepairResult { Success = false, Output = "Другая операция уже выполняется." };
                    GuardStatus status = Inspect();
                    if (!status.IsReady) return new RepairResult { Success = false, Output = status.Summary };
                    string prefix = SpicetifyPrefix();
                    string folder = Path.Combine(Path.GetDirectoryName(status.ConfigPath), "Themes", "GuardStudio");
                    string rollback = Path.Combine(DataDirectory, "backups", DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N"));
                    Directory.CreateDirectory(rollback);
                    File.Copy(status.ConfigPath, Path.Combine(rollback, "config-xpui.ini"));
                    if (Directory.Exists(folder))
                        foreach (string file in Directory.GetFiles(folder)) File.Copy(file, Path.Combine(rollback, Path.GetFileName(file)));
                    ThemeStore.Render(theme, folder, Path.Combine(Path.GetDirectoryName(status.SpotifyPath), "Apps", "xpui"));
                    RepairResult config = RunProcess(status.SpicetifyPath, prefix + "config current_theme GuardStudio color_scheme studio inject_css 1 replace_colors 1 inject_theme_js 0", 30000);
                    if (!config.Success) return config;
                    Log("Применение темы " + theme.Name + ". Резервная копия настроек: " + rollback);
                    RepairResult applied = Repair(false, false, theme);
                    if (applied.Success) ThemeStore.Export(theme, ThemeStore.ActiveFile);
                    else
                    {
                        File.Copy(Path.Combine(rollback, "config-xpui.ini"), status.ConfigPath, true);
                        foreach (string file in Directory.GetFiles(rollback))
                            if (Path.GetFileName(file) != "config-xpui.ini") File.Copy(file, Path.Combine(folder, Path.GetFileName(file)), true);
                        Log("Применение не завершено. Предыдущие настройки темы восстановлены.");
                    }
                    return applied;
                }
                catch (Exception ex) { return new RepairResult { Success = false, Output = ex.Message }; }
                finally { if (owns) mutex.ReleaseMutex(); }
            }
        }

        private static string SpicetifyPrefix()
        {
            if (!IsElevated()) return "";
            if (IsUacDisabled()) return "--bypass-admin ";
            throw new InvalidOperationException("Запустите приложение обычным двойным кликом, без прав администратора.");
        }

        public static RepairResult Repair(bool force)
        {
            return Repair(force, true);
        }

        private static RepairResult Repair(bool force, bool updateCli, ThemeDefinition themeOverride = null)
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
                    if (updateCli)
                    {
                    RepairResult update = RunProcess(status.SpicetifyPath, spicetifyPrefix + "upgrade", 120000);
                    if (update.Success)
                        Log("Проверено обновление Spicetify.");
                    else
                        Log("Не удалось обновить Spicetify, продолжаю установленной версией. " + LimitText(update.Output, 1000));
                    }

                    // `backup apply` refuses to overwrite an existing backup. Restore
                    // the clean app first, then create a fresh backup for the current
                    // Spotify build. This also makes forced repairs idempotent.
                    if (updateCli)
                    {
                    if (!string.IsNullOrWhiteSpace(status.BackupVersion) && VersionCore(status.BackupVersion) == VersionCore(status.SpotifyVersion))
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
                    }

                    string applyCommand = !updateCli && status.PatchPresent && VersionCore(status.BackupVersion) == VersionCore(status.SpotifyVersion)
                        ? "apply --no-restart" : "backup apply --no-restart";
                    RepairResult command = RunProcess(status.SpicetifyPath, spicetifyPrefix + applyCommand, 240000);

                    // Retry the documented clean-restore flow only for a matching
                    // build. Restoring an old backup over a newer client is unsafe.
                    if (!command.Success && command.Output.IndexOf("Please restore first", StringComparison.OrdinalIgnoreCase) >= 0 && VersionCore(status.BackupVersion) == VersionCore(status.SpotifyVersion))
                        command = RunProcess(status.SpicetifyPath, spicetifyPrefix + "restore backup apply --no-restart", 240000);
                    if (command.Success)
                    {
                        // The client chunks are now from the new build. Refresh
                        // generated compatibility selectors before verification.
                        if (status.Theme == "GuardStudio")
                        {
                            ThemeDefinition active = themeOverride;
                            if (active == null && File.Exists(ThemeStore.ActiveFile)) active = ThemeStore.Load(ThemeStore.ActiveFile);
                            if (active != null)
                            {
                                string themeFolder = Path.Combine(Path.GetDirectoryName(status.ConfigPath), "Themes", "GuardStudio");
                                string xpui = Path.Combine(Path.GetDirectoryName(status.SpotifyPath), "Apps", "xpui");
                                ThemeStore.Render(active, themeFolder, xpui);
                                if (!FilesMatch(Path.Combine(themeFolder, "user.css"), Path.Combine(xpui, "user.css")))
                                    command = RunProcess(status.SpicetifyPath, spicetifyPrefix + "apply --no-restart", 240000);
                            }
                        }
                        GuardStatus after = Inspect();
                        if (after.NeedsRepair) command = new RepairResult { Success = false, Output = "Команда завершилась, но проверка файлов не прошла: " + after.Summary };
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

        public static RepairResult InstallDependencies(bool windhawk)
        {
            string script = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "scripts", "install.ps1");
            if (!File.Exists(script)) return new RepairResult { Success = false, Output = "Распакуйте весь архив приложения: scripts/install.ps1 не найден." };
            return RunProcess("powershell.exe", "-NoProfile -ExecutionPolicy Bypass -File \"" + script + "\" -DependenciesOnly" + (windhawk ? " -WithWindhawk" : ""), 1200000);
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


}
