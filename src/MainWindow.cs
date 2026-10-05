using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;

namespace SpicetifyGuard
{
    internal sealed class MainWindow : Window
    {
        private static readonly Brush BackgroundBrush = BrushFrom("#101318");
        private static readonly Brush PanelBrush = BrushFrom("#1A1F27");
        private static readonly Brush Muted = BrushFrom("#A3ADBD");
        private static readonly Brush Accent = BrushFrom("#7FE0B4");
        private ComboBox themePicker;
        private TextBox nameBox, cssBox, logBox;
        private Slider radius;
        private CheckBox compact, branding, autoRepair, withWindhawk;
        private TextBlock themeDescription, statusTitle, statusDetail, feedback;
        private StackPanel previewHost, editor;
        private readonly Dictionary<string, TextBox> colorInputs = new Dictionary<string, TextBox>();
        private readonly List<Button> actionButtons = new List<Button>();
        private ThemeDefinition draft;
        private bool loading, busy;

        public MainWindow()
        {
            Title = "Spicetify Guard Studio";
            Width = 1180; Height = 850; MinWidth = 1000; MinHeight = 650;
            Background = BackgroundBrush; Foreground = Brushes.White;
            FontFamily = new FontFamily("Segoe UI"); FontSize = 14;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Content = Build();
            Loaded += delegate
            {
                ThemeDefinition active = null;
                try { if (File.Exists(ThemeStore.ActiveFile)) active = ThemeStore.Load(ThemeStore.ActiveFile); } catch { }
                ReloadThemes(active == null ? "neutral-light" : active.Id);
                if (active != null) LoadDraft(active);
                RefreshStatus();
            };
        }

        private UIElement Build()
        {
            Grid root = new Grid { Margin = new Thickness(28) };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            StackPanel header = new StackPanel();
            header.Children.Add(Text("GUARD / STUDIO", 12, Accent));
            header.Children.Add(Text("Spotify, в ваших цветах", 30, Brushes.White, true));
            header.Children.Add(Text("Готовые темы, собственная палитра и восстановление после обновлений.", 14, Muted));
            root.Children.Add(header);

            StackPanel health = new StackPanel();
            statusTitle = Text("Проверяю установку…", 16, Accent, true);
            statusDetail = Text("", 12, Muted);
            health.Children.Add(statusTitle); health.Children.Add(statusDetail);
            Border healthCard = Card(health); healthCard.Margin = new Thickness(0, 20, 0, 18);
            Grid.SetRow(healthCard, 1); root.Children.Add(healthCard);

            TabControl tabs = new TabControl { Background = BackgroundBrush, Foreground = Brushes.White, BorderThickness = new Thickness(0) };
            tabs.Items.Add(new TabItem { Header = "  Темы и редактор  ", Content = BuildEditor() });
            tabs.Items.Add(new TabItem { Header = "  Защита и установка  ", Content = BuildProtection() });
            Grid.SetRow(tabs, 2); root.Children.Add(tabs);
            feedback = Text("Изменения в предпросмотре не меняют Spotify. Нажмите «Применить».", 12, Muted);
            feedback.Margin = new Thickness(0, 14, 0, 0);
            Grid.SetRow(feedback, 3); root.Children.Add(feedback);
            return root;
        }

        private UIElement BuildEditor()
        {
            Grid columns = new Grid { Margin = new Thickness(0, 16, 0, 0) };
            columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });
            columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            editor = new StackPanel();
            editor.Children.Add(Text("НАЧНИТЕ С ГОТОВОЙ ТЕМЫ", 12, Muted, true));
            themePicker = new ComboBox { Margin = new Thickness(0, 10, 0, 4), Height = 36, Foreground = Brushes.Black };
            themePicker.SelectionChanged += delegate
            {
                ThemeDefinition selected = themePicker.SelectedItem as ThemeDefinition;
                if (selected != null) LoadDraft(selected);
            };
            editor.Children.Add(themePicker);
            themeDescription = Text("", 12, Muted); editor.Children.Add(themeDescription);
            editor.Children.Add(Label("Название вашей темы"));
            nameBox = Input(); editor.Children.Add(nameBox);
            nameBox.TextChanged += delegate { UpdateDraft(); };
            editor.Children.Add(Label("Палитра · HEX"));
            Grid colors = new Grid();
            colors.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            colors.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            colors.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            for (int i = 0; i < ThemeStore.ColorKeys.Length; i++)
            {
                string key = ThemeStore.ColorKeys[i];
                colors.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                TextBlock label = Text(ThemeStore.ColorLabels[i], 13, Muted);
                label.VerticalAlignment = VerticalAlignment.Center;
                Grid.SetRow(label, i); colors.Children.Add(label);
                TextBox value = Input(); value.Width = 95; value.MaxLength = 7; value.Margin = new Thickness(8, 3, 4, 3);
                value.TextChanged += delegate { UpdateDraft(); };
                colorInputs[key] = value; Grid.SetRow(value, i); Grid.SetColumn(value, 1); colors.Children.Add(value);
                Button choose = Button("◉", delegate { ChooseColor(key); }, false);
                choose.Padding = new Thickness(10, 3, 10, 3); choose.Margin = new Thickness(0, 3, 0, 3);
                choose.ToolTip = "Выбрать цвет"; Grid.SetRow(choose, i); Grid.SetColumn(choose, 2); colors.Children.Add(choose);
            }
            editor.Children.Add(colors);
            editor.Children.Add(Label("Скругление · 0–24 px"));
            radius = new Slider { Minimum = 0, Maximum = 24, IsSnapToTickEnabled = true, TickFrequency = 1, Margin = new Thickness(0, 8, 0, 12) };
            radius.ValueChanged += delegate { UpdateDraft(); }; editor.Children.Add(radius);
            compact = new CheckBox { Content = "Компактные строки треков", Margin = new Thickness(0, 0, 0, 10) };
            branding = new CheckBox { Content = "Скрыть логотип Spotify", Margin = new Thickness(0, 0, 0, 8) };
            compact.Checked += delegate { UpdateDraft(); }; compact.Unchecked += delegate { UpdateDraft(); };
            branding.Checked += delegate { UpdateDraft(); }; branding.Unchecked += delegate { UpdateDraft(); };
            editor.Children.Add(compact); editor.Children.Add(branding);
            editor.Children.Add(Label("Дополнительный CSS"));
            cssBox = Input(); cssBox.AcceptsReturn = true; cssBox.AcceptsTab = true; cssBox.Height = 150;
            cssBox.FontFamily = new FontFamily("Consolas"); cssBox.FontSize = 12;
            cssBox.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            cssBox.TextChanged += delegate { UpdateDraft(); }; editor.Children.Add(cssBox);
            editor.Children.Add(Text("CSS применяется в Spotify. Макет справа показывает палитру, скругления и плотность.", 12, Muted));
            ScrollViewer scroll = new ScrollViewer { Content = editor, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(0, 0, 12, 0) };
            columns.Children.Add(scroll);

            StackPanel right = new StackPanel();
            right.Children.Add(Text("ПРЕДПРОСМОТР", 12, Muted, true));
            previewHost = new StackPanel { Margin = new Thickness(0, 12, 0, 16) }; right.Children.Add(previewHost);
            right.Children.Add(Button("Применить в Spotify", async delegate { if (ValidDraft()) await Run("Применяю тему…", delegate { return GuardService.ApplyTheme(ThemeStore.Clone(draft)); }); }, true));
            right.Children.Add(Button("Сохранить свою тему", delegate { SaveDraft(); }, false));
            WrapPanel exchange = new WrapPanel();
            exchange.Children.Add(Button("Импорт JSON", delegate { ImportTheme(); }, false));
            exchange.Children.Add(Button("Экспорт JSON", delegate { ExportTheme(); }, false));
            right.Children.Add(exchange);
            right.Children.Add(Text("Применение перезапустит Spotify, если он сейчас открыт. Ваши настройки и темы сохраняются в резервную копию.", 12, Muted));
            // Keep actions visible on a smaller screen; the preview scrolls
            // instead of pushing the Apply button below the window.
            right.Children.Remove(previewHost);
            UIElement previewHeading = right.Children[0]; right.Children.RemoveAt(0);
            Grid rightGrid = new Grid();
            rightGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            rightGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            rightGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            rightGrid.Children.Add(previewHeading);
            ScrollViewer previewScroll = new ScrollViewer { Content = previewHost, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            Grid.SetRow(previewScroll, 1); rightGrid.Children.Add(previewScroll);
            Grid.SetRow(right, 2); rightGrid.Children.Add(right);
            Grid.SetColumn(rightGrid, 2); columns.Children.Add(rightGrid);
            return columns;
        }

        private UIElement BuildProtection()
        {
            StackPanel panel = new StackPanel { Margin = new Thickness(0, 20, 0, 0) };
            panel.Children.Add(Text("Тема переживёт обновление", 22, Brushes.White, true));
            panel.Children.Add(Text("Guard проверяет версию Spotify, наличие патча, палитру и CSS. Если файлы заменены обновлением, он применяет тему заново.", 14, Muted));
            autoRepair = new CheckBox { Content = "Проверять и восстанавливать каждые 5 минут", Margin = new Thickness(0, 18, 0, 8) };
            autoRepair.Checked += AutoChanged; autoRepair.Unchecked += AutoChanged; panel.Children.Add(autoRepair);
            panel.Children.Add(Button("Восстановить патч сейчас", async delegate { await Run("Восстанавливаю Spotify…", delegate { return GuardService.Repair(true); }); }, true));
            panel.Children.Add(Button("Проверить состояние", delegate { RefreshStatus(); }, false));
            panel.Children.Add(Label("Первичная установка"));
            panel.Children.Add(Text("Установка зависимостей использует Windows Package Manager. Для новой установки Spotify сначала откройте его, войдите и подождите около минуты, затем примените тему.", 13, Muted));
            withWindhawk = new CheckBox { Content = "Также установить Windhawk (для оформления рамки окна)", Margin = new Thickness(0, 12, 0, 8) }; panel.Children.Add(withWindhawk);
            panel.Children.Add(Button("Установить недостающие компоненты", async delegate { bool windhawk = withWindhawk.IsChecked == true; await Run("Устанавливаю компоненты…", delegate { return GuardService.InstallDependencies(windhawk); }); }, false));
            panel.Children.Add(Text("Windhawk не нужен для цветов. Мод для рамки включается в Windhawk отдельно; ссылка и инструкция находятся в README.", 12, Muted));
            panel.Children.Add(Button("Открыть инструкцию", delegate { OpenFile(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "README.md")); }, false));
            panel.Children.Add(Label("Последние события"));
            logBox = Input(); logBox.IsReadOnly = true; logBox.Height = 160; logBox.TextWrapping = TextWrapping.Wrap;
            logBox.FontFamily = new FontFamily("Consolas"); logBox.FontSize = 11;
            logBox.VerticalScrollBarVisibility = ScrollBarVisibility.Auto; panel.Children.Add(logBox);
            panel.Children.Add(Button("Папка логов и резервных копий", delegate { GuardService.OpenLogFolder(); }, false));
            return new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        }

        private void LoadDraft(ThemeDefinition theme)
        {
            loading = true; draft = ThemeStore.Clone(theme);
            nameBox.Text = draft.Name; themeDescription.Text = draft.Description;
            foreach (string key in ThemeStore.ColorKeys) colorInputs[key].Text = "#" + draft.Colors[key].ToUpperInvariant();
            radius.Value = draft.Radius; compact.IsChecked = draft.Compact; branding.IsChecked = draft.HideBranding; cssBox.Text = draft.CustomCss ?? "";
            loading = false; DrawPreview();
        }

        private void UpdateDraft()
        {
            if (loading || draft == null) return;
            draft.Name = nameBox.Text;
            foreach (string key in ThemeStore.ColorKeys) draft.Colors[key] = colorInputs[key].Text.Trim().TrimStart('#');
            draft.Radius = (int)radius.Value; draft.Compact = compact.IsChecked == true; draft.HideBranding = branding.IsChecked == true; draft.CustomCss = cssBox.Text;
            try { ThemeStore.Validate(draft); DrawPreview(); feedback.Text = "Предпросмотр обновлён. Нажмите «Применить», чтобы изменить Spotify."; }
            catch (Exception ex) { feedback.Text = ex.Message; }
        }

        private void DrawPreview()
        {
            if (draft == null) return;
            Func<string, Brush> color = key => BrushFrom("#" + draft.Colors[key]);
            previewHost.Children.Clear();
            Border outer = new Border { Background = color("main"), CornerRadius = new CornerRadius(draft.Radius), Padding = new Thickness(18) };
            StackPanel content = new StackPanel();
            content.Children.Add(Text("⌂    Поиск музыки                     • • •", 12, color("subtext")));
            TextBlock title = Text(draft.Name, 25, color("text"), true); title.Margin = new Thickness(0, 28, 0, 6); content.Children.Add(title);
            content.Children.Add(Text("Ваша музыка. Ваше настроение.", 12, color("subtext")));
            WrapPanel covers = new WrapPanel { Margin = new Thickness(0, 18, 0, 20) };
            foreach (string label in new[] { "♥  Любимые", "♫  Открытия", "♪  Микс" })
            {
                Border tile = new Border { Background = color("card"), CornerRadius = new CornerRadius(draft.Radius), Padding = new Thickness(12), Margin = new Thickness(0, 0, 8, 8) };
                tile.Child = Text(label, 12, color("text"), true); covers.Children.Add(tile);
            }
            content.Children.Add(covers);
            content.Children.Add(Text("Сейчас в вашей медиатеке", 17, color("text"), true));
            int track = 0;
            foreach (string titleText in new[] { "Late night drive", "Soft focus", "City lights" })
            {
                Border row = new Border { Background = color(track == 1 ? "card" : "main"), CornerRadius = new CornerRadius(draft.Radius), Padding = new Thickness(8, draft.Compact ? 5 : 12, 8, draft.Compact ? 5 : 12), Margin = new Thickness(0, 4, 0, 0) };
                StackPanel text = new StackPanel(); text.Children.Add(Text((++track) + "    " + titleText + "                      3:24", 13, color("text"))); text.Children.Add(Text("       Guard Studio", 11, color("subtext"))); row.Child = text; content.Children.Add(row);
            }
            Border player = new Border { Background = color("player"), Margin = new Thickness(0, 18, 0, 0), Padding = new Thickness(8) };
            StackPanel playerContent = new StackPanel();
            Border play = new Border { Background = color("button"), CornerRadius = new CornerRadius(18), Padding = new Thickness(12, 6, 12, 6), HorizontalAlignment = HorizontalAlignment.Center };
            play.Child = Text("◀     ▶     ▶", 14, color("shadow"), true); playerContent.Children.Add(play);
            playerContent.Children.Add(new Border { Height = 3, Background = color("button-disabled"), Margin = new Thickness(0, 12, 0, 0) }); player.Child = playerContent;
            content.Children.Add(player); outer.Child = content; previewHost.Children.Add(outer);
        }

        private void ChooseColor(string key)
        {
            using (System.Windows.Forms.ColorDialog picker = new System.Windows.Forms.ColorDialog())
            {
                try { picker.Color = System.Drawing.ColorTranslator.FromHtml(colorInputs[key].Text); } catch { }
                picker.FullOpen = true;
                if (picker.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                    colorInputs[key].Text = "#" + picker.Color.R.ToString("X2") + picker.Color.G.ToString("X2") + picker.Color.B.ToString("X2");
            }
        }

        private bool ValidDraft()
        {
            try { ThemeStore.Validate(draft); return true; }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "Проверьте тему", MessageBoxButton.OK, MessageBoxImage.Warning); return false; }
        }

        private void SaveDraft()
        {
            if (!ValidDraft()) return;
            ThemeDefinition saved = ThemeStore.Clone(draft);
            if (!saved.Id.StartsWith("user-")) saved.Id = "user-" + Guid.NewGuid().ToString("N");
            try { ThemeStore.Save(saved); ReloadThemes(saved.Id); feedback.Text = "Тема сохранена. Её можно выбрать снова или экспортировать."; }
            catch (Exception ex) { ShowError(ex); }
        }

        private void ReloadThemes(string id)
        {
            List<ThemeDefinition> themes = ThemeStore.All(); themePicker.ItemsSource = themes;
            themePicker.SelectedItem = themes.FirstOrDefault(t => t.Id == id) ?? themes.FirstOrDefault();
            if (themes.Count == 0) feedback.Text = "Папка themes отсутствует рядом с приложением. Распакуйте весь архив.";
        }

        private void ImportTheme()
        {
            OpenFileDialog dialog = new OpenFileDialog { Filter = "Guard Studio theme (*.json)|*.json" };
            if (dialog.ShowDialog(this) != true) return;
            try { ThemeDefinition imported = ThemeStore.Load(dialog.FileName); imported.Id = "user-" + Guid.NewGuid().ToString("N"); ThemeStore.Save(imported); ReloadThemes(imported.Id); feedback.Text = "Тема импортирована. Проверьте палитру и примените её."; }
            catch (Exception ex) { ShowError(ex); }
        }

        private void ExportTheme()
        {
            if (!ValidDraft()) return;
            SaveFileDialog dialog = new SaveFileDialog { Filter = "Guard Studio theme (*.json)|*.json", FileName = draft.Id + ".json" };
            if (dialog.ShowDialog(this) != true) return;
            try { ThemeStore.Export(draft, dialog.FileName); feedback.Text = "Тема экспортирована."; }
            catch (Exception ex) { ShowError(ex); }
        }

        private async void AutoChanged(object sender, RoutedEventArgs args)
        {
            if (loading || busy) return;
            bool enabled = autoRepair.IsChecked == true;
            await Run(enabled ? "Включаю защиту…" : "Отключаю защиту…", delegate { return GuardService.SetAutoRepair(enabled); });
        }

        private async Task Run(string label, Func<RepairResult> action)
        {
            if (busy) return; busy = true;
            foreach (Button button in actionButtons) button.IsEnabled = false;
            editor.IsEnabled = false; autoRepair.IsEnabled = false; withWindhawk.IsEnabled = false;
            feedback.Text = label;
            try
            {
                RepairResult result = await Task.Factory.StartNew(action);
                feedback.Text = result.Success ? "Готово. Настройки применены." : "Операция не завершена. Подробности в журнале.";
                if (!result.Success) MessageBox.Show(this, result.Output.Length > 3500 ? result.Output.Substring(result.Output.Length - 3500) : result.Output, "Guard Studio", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            catch (Exception ex) { ShowError(ex); }
            finally { busy = false; foreach (Button button in actionButtons) button.IsEnabled = true; editor.IsEnabled = true; autoRepair.IsEnabled = true; withWindhawk.IsEnabled = true; RefreshStatus(); }
        }

        private void RefreshStatus()
        {
            GuardStatus status = GuardService.Inspect(); statusTitle.Text = status.Summary;
            statusTitle.Foreground = status.NeedsRepair || !status.IsReady ? BrushFrom("#FFCA81") : Accent;
            statusDetail.Text = "Spotify " + status.SpotifyVersion + " · тема " + (string.IsNullOrEmpty(status.Theme) ? "не выбрана" : status.Theme) + " · " + (status.PatchPresent ? "инъекция найдена" : "инъекции нет");
            loading = true; autoRepair.IsChecked = GuardService.IsAutoRepairEnabled(); loading = false;
            logBox.Text = GuardService.ReadRecentLog(); logBox.ScrollToEnd();
        }

        private void ShowError(Exception ex) { MessageBox.Show(this, ex.Message, "Guard Studio", MessageBoxButton.OK, MessageBoxImage.Warning); }
        private static void OpenFile(string path) { if (File.Exists(path)) Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        private static TextBlock Text(string value, double size, Brush brush, bool bold = false) { return new TextBlock { Text = value, FontSize = size, Foreground = brush, FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 3) }; }
        private static TextBlock Label(string value) { TextBlock label = Text(value, 13, Brushes.White, true); label.Margin = new Thickness(0, 18, 0, 7); return label; }
        private static Brush BrushFrom(string hex) { return new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)); }
        private static Border Card(UIElement child) { return new Border { Background = PanelBrush, CornerRadius = new CornerRadius(12), Padding = new Thickness(18), Child = child }; }
        private static TextBox Input() { return new TextBox { Background = BrushFrom("#242B36"), Foreground = Brushes.White, BorderBrush = BrushFrom("#384253"), Padding = new Thickness(8, 5, 8, 5), Margin = new Thickness(0, 2, 0, 4) }; }
        private Button Button(string value, Action action, bool primary)
        {
            Button button = new Button { Content = value, Background = primary ? Accent : BrushFrom("#29323F"), Foreground = primary ? BackgroundBrush : Brushes.White, BorderThickness = new Thickness(0), Padding = new Thickness(18, 10, 18, 10), Margin = new Thickness(0, 4, 8, 8), HorizontalContentAlignment = HorizontalAlignment.Center, Cursor = System.Windows.Input.Cursors.Hand };
            button.Click += delegate { action(); }; actionButtons.Add(button); return button;
        }
    }
}
