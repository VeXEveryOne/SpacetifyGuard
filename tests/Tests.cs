using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Web.Script.Serialization;

namespace SpicetifyGuard
{
    internal static class Tests
    {
        private static int checks;
        private static void Check(bool condition, string message) { checks++; if (!condition) throw new Exception(message); }
        private static void Reject(Action action, string message)
        {
            bool rejected = false; try { action(); } catch (InvalidDataException) { rejected = true; }
            Check(rejected, message);
        }
        private static int Main()
        {
            string folder = Path.Combine(Path.GetTempPath(), "GuardStudioTests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            try
            {
                List<ThemeDefinition> themes = Directory.GetFiles(ThemeStore.Assets, "*.json").Select(ThemeStore.Load).ToList();
                Check(themes.Count >= 3, "Three presets must ship.");
                foreach (ThemeDefinition theme in themes)
                {
                    string rendered = Path.Combine(folder, theme.Id); ThemeStore.Render(theme, rendered);
                    string ini = File.ReadAllText(Path.Combine(rendered, "color.ini"));
                    string css = File.ReadAllText(Path.Combine(rendered, "user.css"));
                    Check(ThemeStore.ColorKeys.All(k => ini.Contains(k + " = " + theme.Colors[k])), "All colors must render.");
                    Check(css.Contains("--br-1: " + theme.Radius + "px"), "Radius must render.");
                    Check(Contrast(theme.Colors["text"], theme.Colors["main"]) >= 7, "Preset main text contrast must exceed 7:1.");
                    Check(Contrast(theme.Colors["subtext"], theme.Colors["main"]) >= 4.5, "Preset secondary text contrast must exceed 4.5:1.");
                    string json = Path.Combine(folder, theme.Id + ".json"); ThemeStore.Export(theme, json);
                    ThemeDefinition roundtrip = ThemeStore.Load(json);
                    Check(roundtrip.Colors["main"] == theme.Colors["main"] && roundtrip.Id == theme.Id, "Import/export must retain data.");
                }
                ThemeDefinition draft = ThemeStore.Clone(themes[0]);
                draft.Id = "../escape"; Reject(delegate { ThemeStore.Validate(draft); }, "Reject traversal IDs.");
                draft.Id = "custom"; draft.Colors["main"] = "red; url(x)"; Reject(delegate { ThemeStore.Validate(draft); }, "Reject color injection.");
                draft.Colors["main"] = "FFFFFF"; draft.Radius = 25; Reject(delegate { ThemeStore.Validate(draft); }, "Reject out of range radius.");
                draft.Radius = 12; draft.FormatVersion = 2; Reject(delegate { ThemeStore.Validate(draft); }, "Reject unknown formats.");
                draft.FormatVersion = 1; draft.Compact = true; draft.CustomCss = ".test { opacity: .8; }";
                string custom = ThemeStore.RenderCss(draft, "/* base */");
                Check(custom.Contains("height: 44px") && custom.EndsWith(draft.CustomCss + "\n"), "Custom CSS and compact mode must render.");
                string modules = Path.Combine(folder, "modules"); Directory.CreateDirectory(modules);
                File.WriteAllText(Path.Combine(modules, "dwp-home-header.css"), ".newHash123{background-image:linear-gradient(#0009,#fff);height:256px;position:absolute}");
                File.WriteAllText(Path.Combine(modules, "dwp-home-chips-row.css"), ".newCarousel .content{--carousel-end-chevron-gradient:#121212b3;} .header.newBanner{height:40vh;max-height:none}");
                string compatibility = ThemeStore.CompatibilityCss(modules);
                Check(compatibility.Contains(".newHash123 { display: none !important;"), "Detect newly hashed home gradients.");
                Check(compatibility.Contains(".newCarousel .content { --carousel-start-chevron-gradient: var(--spice-main)"), "Override dark carousel fades using the actual build's selector.");
                Check(compatibility.Contains(".header.newBanner .contentSpacing * { color: #fff !important"), "Keep full-bleed header text readable after class hashes change.");
                Check(ThemeStore.RenderCss(themes[0], "").Contains("[data-shelf=\"carousel\"]"), "Carousel fades must follow the palette across route chunks.");
                Check(GuardService.ChooseApplyCommand("1.3.3.264.gtest", "1.3.3.264", true) == "apply --no-restart", "Same-build repair must reuse the prepared backup.");
                Check(GuardService.ChooseApplyCommand("1.3.3.264.gtest", "1.3.4.1", true) == "backup apply --no-restart", "A new Spotify build must create its own backup.");
                Check(GuardService.ChooseApplyCommand("1.3.3.264.gtest", "1.3.3.264", false) == "backup apply --no-restart", "A CLI upgrade must refresh preprocessing.");

                string config = Path.Combine(folder, "config-xpui.ini"), cli = Path.Combine(folder, "spicetify.exe"), spotify = Path.Combine(folder, "Spotify");
                Directory.CreateDirectory(spotify); File.WriteAllText(cli, "fake"); File.WriteAllText(Path.Combine(spotify, "Spotify.exe"), "fake");
                string themePath = Path.Combine(folder, "Themes", "Mock"), xpui = Path.Combine(spotify, "Apps", "xpui");
                Directory.CreateDirectory(themePath); Directory.CreateDirectory(Path.Combine(xpui, "helper"));
                File.WriteAllText(config, "[Setting]\nspotify_path = " + spotify + "\ncurrent_theme = Mock\ncolor_scheme = studio\n[Backup]\nversion = 1.3.3.264.gtest\n");
                File.WriteAllText(Path.Combine(themePath, "color.ini"), ThemeStore.RenderColors(themes[0]));
                File.WriteAllText(Path.Combine(themePath, "user.css"), "/* healthy */");
                File.WriteAllText(Path.Combine(xpui, "user.css"), "/* healthy */");
                File.WriteAllText(Path.Combine(xpui, "helper", "spicetifyWrapper.js"), "/* injection */");
                Dictionary<string, object> injected = new Dictionary<string, object> {
                    {"theme_name", "Mock"}, {"scheme_name", "studio"},
                    {"schemes", new Dictionary<string, object> {{"studio", themes[0].Colors}}}
                };
                string injectedPath = Path.Combine(xpui, "spicetify-config.json");
                File.WriteAllText(injectedPath, new JavaScriptSerializer().Serialize(injected));
                Check(!GuardService.InspectAt(config, cli, "1.3.3.264").NeedsRepair, "Matching files, CSS, palette and version must be healthy.");
                Check(GuardService.InspectAt(config, cli, "1.3.4.1").NeedsRepair, "Spotify updates must trigger repair.");
                File.Delete(Path.Combine(xpui, "helper", "spicetifyWrapper.js"));
                Check(GuardService.InspectAt(config, cli, "1.3.3.264").NeedsRepair, "Same-version missing injection must trigger repair.");
                File.WriteAllText(Path.Combine(xpui, "helper", "spicetifyWrapper.js"), "/* injection */");
                File.WriteAllText(Path.Combine(xpui, "user.css"), "/* modified */");
                Check(GuardService.InspectAt(config, cli, "1.3.3.264").NeedsRepair, "Same-version stale CSS must trigger repair.");
                File.WriteAllText(Path.Combine(xpui, "user.css"), "/* healthy */");
                injected["theme_name"] = "Other"; File.WriteAllText(injectedPath, new JavaScriptSerializer().Serialize(injected));
                Check(GuardService.InspectAt(config, cli, "1.3.3.264").NeedsRepair, "Wrong active theme must trigger repair.");
                Console.WriteLine("PASS: " + checks + " checks (presets, contrast, roundtrip, validation, rendering and guard health)."); return 0;
            }
            catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
            finally { Directory.Delete(folder, true); }
        }
        private static double Luminance(string hex)
        {
            double[] channels = new[] { 0, 2, 4 }.Select(i => Convert.ToInt32(hex.Substring(i, 2), 16) / 255.0).Select(c => c <= .04045 ? c / 12.92 : Math.Pow((c + .055) / 1.055, 2.4)).ToArray();
            return channels[0] * .2126 + channels[1] * .7152 + channels[2] * .0722;
        }
        private static double Contrast(string first, string second) { double a = Luminance(first), b = Luminance(second); return (Math.Max(a, b) + .05) / (Math.Min(a, b) + .05); }
    }
}
