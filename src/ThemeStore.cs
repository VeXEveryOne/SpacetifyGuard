using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

namespace SpicetifyGuard
{
    public sealed class ThemeDefinition
    {
        public int FormatVersion { get; set; }
        public string Id { get; set; }
        public string Name { get; set; }
        public string Description { get; set; }
        public Dictionary<string, string> Colors { get; set; }
        public int Radius { get; set; }
        public bool Compact { get; set; }
        public bool HideBranding { get; set; }
        public string CustomCss { get; set; }
        public override string ToString() { return Name; }
    }

    internal static class ThemeStore
    {
        public static readonly string[] ColorKeys = { "text", "subtext", "main", "sidebar", "player", "card", "shadow", "selected-row", "button", "button-active", "button-disabled", "tab-active", "notification", "notification-error", "misc" };
        public static readonly string[] ColorLabels = { "Основной текст", "Вторичный текст", "Основной фон", "Боковая панель", "Плеер", "Карточки", "Текст активных элементов", "Выбранная строка", "Кнопки / акцент", "Кнопки при наведении", "Неактивные кнопки", "Активная вкладка", "Уведомления", "Ошибки", "Дополнительный фон" };
        public static string Assets { get { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "themes"); } }
        public static string UserDirectory { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SpicetifyGuard", "themes"); } }
        public static string ActiveFile { get { return Path.Combine(Path.GetDirectoryName(UserDirectory), "active-theme.json"); } }

        public static void Validate(ThemeDefinition theme)
        {
            if (theme == null || theme.FormatVersion != 1) throw new InvalidDataException("Неподдерживаемый формат темы (нужен FormatVersion: 1).");
            if (string.IsNullOrWhiteSpace(theme.Id) || !Regex.IsMatch(theme.Id, @"^[a-z0-9][a-z0-9-]{0,47}$")) throw new InvalidDataException("Некорректный идентификатор темы.");
            if (string.IsNullOrWhiteSpace(theme.Name) || theme.Name.Length > 80) throw new InvalidDataException("Название должно содержать от 1 до 80 символов.");
            if (theme.Radius < 0 || theme.Radius > 24) throw new InvalidDataException("Скругление должно быть от 0 до 24 px.");
            if (theme.Colors == null || theme.Colors.Count != ColorKeys.Length) throw new InvalidDataException("В теме должны быть все 15 цветов Spicetify.");
            foreach (string key in ColorKeys)
            {
                string color;
                if (!theme.Colors.TryGetValue(key, out color) || color == null || !Regex.IsMatch(color, @"^[0-9A-Fa-f]{6}$")) throw new InvalidDataException("Цвет " + key + " должен быть шестизначным HEX без #.");
            }
            if ((theme.CustomCss ?? "").Length > 131072) throw new InvalidDataException("Дополнительный CSS превышает 128 КБ.");
        }

        public static ThemeDefinition Load(string path)
        {
            if (new FileInfo(path).Length > 1024 * 1024) throw new InvalidDataException("Файл темы превышает 1 МБ.");
            ThemeDefinition theme = new JavaScriptSerializer().Deserialize<ThemeDefinition>(File.ReadAllText(path, Encoding.UTF8));
            Validate(theme);
            return theme;
        }

        public static ThemeDefinition Clone(ThemeDefinition theme)
        {
            JavaScriptSerializer json = new JavaScriptSerializer();
            return json.Deserialize<ThemeDefinition>(json.Serialize(theme));
        }

        public static void Export(ThemeDefinition theme, string path)
        {
            Validate(theme);
            AtomicWrite(path, new JavaScriptSerializer().Serialize(theme));
        }

        public static string Save(ThemeDefinition theme)
        {
            Validate(theme);
            Directory.CreateDirectory(UserDirectory);
            string path = Path.Combine(UserDirectory, theme.Id + ".json");
            Export(theme, path);
            return path;
        }

        public static List<ThemeDefinition> All()
        {
            List<ThemeDefinition> themes = new List<ThemeDefinition>();
            foreach (string folder in new[] { Assets, UserDirectory })
            {
                if (!Directory.Exists(folder)) continue;
                foreach (string file in Directory.GetFiles(folder, "*.json").OrderBy(p => p))
                {
                    try { themes.Add(Load(file)); }
                    catch (Exception ex) { GuardService.Log("Пропускаю некорректную тему " + Path.GetFileName(file) + ": " + ex.Message); }
                }
            }
            return themes;
        }

        public static string RenderColors(ThemeDefinition theme)
        {
            Validate(theme);
            StringBuilder text = new StringBuilder("[studio]\n");
            foreach (string key in ColorKeys) text.Append(key).Append(" = ").Append(theme.Colors[key].ToUpperInvariant()).Append('\n');
            return text.ToString();
        }

        public static string RenderCss(ThemeDefinition theme, string baseCss, string spotifyCssDirectory = null)
        {
            Validate(theme);
            StringBuilder css = new StringBuilder(baseCss);
            css.Append("\n/* Guard Studio: generated appearance overrides */\n:root { --br-1: ").Append(theme.Radius).Append("px; --br-2: ").Append(Math.Max(0, theme.Radius - 2)).Append("px; }\n");
            css.Append(".view-homeShortcutsGrid-shortcut { background: var(--spice-card) !important; }\n.view-homeShortcutsGrid-shortcut:hover { background: var(--spice-button-disabled) !important; }\n");
            // The original light Ziro theme hard-coded pale shortcut cards.
            css.Append("[data-testid=\"home-page\"] [data-testid=\"shortcut\"] { background: var(--spice-card) !important; }\n");
            css.Append("[data-shelf=\"carousel\"] { --carousel-start-chevron-gradient: var(--spice-main) !important; --carousel-end-chevron-gradient: var(--spice-main) !important; }\n");
            css.Append(".main-entityHeader-container.main-entityHeader-withBackgroundImage .main-entityHeader-contentWrapper, .main-entityHeader-container.main-entityHeader-withBackgroundImage .main-entityHeader-contentWrapper * { color: #fff !important; --text-base: #fff !important; --text-subdued: #ddd !important; }\n");
            css.Append("[data-encore-id=\"logoSpotify\"], .premiumSpotifyIcon { display: ").Append(theme.HideBranding ? "none" : "revert").Append(" !important; }\n");
            if (theme.Compact) css.Append(".main-trackList-trackListRow { min-height: 44px !important; height: 44px !important; }\n");
            css.Append(CompatibilityCss(spotifyCssDirectory));
            css.Append("\n/* User CSS */\n").Append(theme.CustomCss ?? "").Append('\n');
            return css.ToString();
        }

        internal static string CompatibilityCss(string directory)
        {
            StringBuilder css = new StringBuilder("\n/* Build-specific Spotify compatibility */\n");
            if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory)) return css.ToString();
            foreach (string module in new[] { "dwp-home-header.css", "dwp-home-chips-row.css", "dwp-panel-section.css" })
            {
                string file = Path.Combine(directory, module);
                if (!File.Exists(file)) continue;
                foreach (Match rule in Regex.Matches(File.ReadAllText(file), @"([^{}]+)\{([^{}]*)\}"))
                {
                    string selector = rule.Groups[1].Value.Trim(), properties = rule.Groups[2].Value;
                    // Accept only ordinary CSS selectors, not nested @ rules.
                    if (selector.StartsWith("@") || selector.Contains(";") || selector.Length > 400) continue;
                    if (module == "dwp-home-header.css" && properties.Contains("height:256px") && properties.Contains("background-image:"))
                        css.Append(selector).Append(" { display: none !important; background: none !important; }\n");
                    if (module == "dwp-home-chips-row.css" && properties.Contains("--carousel-end-chevron-gradient:"))
                        css.Append(selector).Append(" { --carousel-start-chevron-gradient: var(--spice-main) !important; --carousel-end-chevron-gradient: var(--spice-main) !important; }\n");
                    if (module == "dwp-home-chips-row.css" && properties.Contains("background-color:#0009"))
                        css.Append(selector).Append(" { background: var(--spice-main) !important; }\n");
                    // Modern full-bleed headers no longer have the translated
                    // main-entityHeader-withBackgroundImage class. Their banner
                    // variant is identified from the current build's own rule.
                    if (module == "dwp-home-chips-row.css" && properties.Contains("height:40vh") && properties.Contains("max-height:none"))
                    {
                        css.Append(selector).Append(" .contentSpacing, ").Append(selector).Append(" .contentSpacing * { color: #fff !important; --text-base: #fff !important; --text-subdued: #ddd !important; }\n");
                        css.Append(selector).Append(" [data-testid=\"entityTitle\"] { text-shadow: 0 1px 6px rgba(0,0,0,.5); }\n");
                    }
                    if (module == "dwp-panel-section.css" && (selector.Contains(":before") || selector.Contains(":after")) && properties.Contains("background:linear-gradient(#121212"))
                        css.Append(selector).Append(" { background: none !important; }\n");
                }
            }
            return css.ToString();
        }

        public static void Render(ThemeDefinition theme, string outputDirectory, string spotifyCssDirectory = null)
        {
            Validate(theme);
            string baseCss = File.ReadAllText(Path.Combine(Assets, "base.css"), Encoding.UTF8);
            Directory.CreateDirectory(outputDirectory);
            AtomicWrite(Path.Combine(outputDirectory, "color.ini"), RenderColors(theme));
            AtomicWrite(Path.Combine(outputDirectory, "user.css"), RenderCss(theme, baseCss, spotifyCssDirectory));
        }

        public static void AtomicWrite(string path, string contents)
        {
            string fullPath = Path.GetFullPath(path);
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath));
            string temporary = fullPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(temporary, contents, new UTF8Encoding(false));
                if (File.Exists(fullPath)) File.Replace(temporary, fullPath, null);
                else File.Move(temporary, fullPath);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
    }
}
