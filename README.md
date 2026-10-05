# Spicetify Guard Studio

Редактор тем Spotify для Windows и защита патча Spicetify после обновлений. Исходники находятся в отдельном проекте `SpacetifyGuard`; интерфейс называется **Spicetify Guard Studio**.

## Что умеет

- Три готовые темы: **Neutral Light** (светлая ZiroNeutral), **Graphite Dark** и **Midnight Blue**.
- Редактор 15 цветов, скруглений, плотности списка треков и дополнительного CSS.
- Предпросмотр палитры до применения. Это макет, а не встроенный Spotify: произвольный CSS виден после применения в клиенте.
- Сохранение пользовательских тем, импорт и экспорт JSON.
- Применение темы в Spotify с резервной копией предыдущих настроек.
- Фоновая проверка каждые 5 минут через Планировщик Windows. Проверяются версия клиента, JS-инъекция, CSS и палитра — одна запись версии в конфиге не считается доказательством исправного патча.
- Ремонт обновляет Spicetify и выбирает корректную последовательность backup/apply. Старый backup никогда не восстанавливается поверх другой версии клиента.

## Установка

1. Скачайте `SpicetifyGuard-Studio-win-x64.zip` из [Releases](https://github.com/VeXEveryOne/SpacetifyGuard/releases) и распакуйте **весь** архив.
2. Запустите `Install.cmd` обычным двойным кликом. Он установит отсутствующие Spotify и Spicetify через winget, скопирует приложение в `%LOCALAPPDATA%\Programs\SpicetifyGuardStudio`, создаст ярлыки и включит фоновую защиту.
3. На новой установке откройте Spotify, войдите в аккаунт и оставьте клиент открытым примерно на минуту, чтобы создался файл prefs. Затем запустите Guard Studio, выберите тему и нажмите **Применить в Spotify**.

Spotify будет перезапущен при применении или ремонте, если он уже работал. Приложение не требует прав администратора. Если UAC отключён самой системой, Guard добавляет флаг Spicetify `--bypass-admin`; при обычном повышенном запуске с включённым UAC применение блокируется.

### Windhawk

Цвета, карточки и список треков работают через Spicetify. **Windhawk необязателен**: он нужен, если хотите менять нативную рамку/заголовок окна Spotify.

Для установки вместе с зависимостями:

```powershell
.\Install.cmd -WithWindhawk
```

Либо в Guard Studio откройте **Защита и установка**, отметьте Windhawk и нажмите **Установить недостающие компоненты**. Программа установит Windhawk; отдельные моды не включаются автоматически. Для рамки окна найдите мод [cef-titlebar-enabler-universal](https://github.com/ramensoftware/windhawk-mods/blob/main/mods/cef-titlebar-enabler-universal.wh.cpp), изучите его описание и включите подходящие опции. Существующие моды и настройки Windhawk сохраняются.

### Требования и ручная установка

| Компонент | Как получить |
| --- | --- |
| Windows 10/11 x64, .NET Framework 4.8 | Обычно уже есть в Windows. При отсутствии: [Microsoft .NET Framework 4.8](https://dotnet.microsoft.com/download/dotnet-framework/net48) |
| Windows Package Manager (winget) | [App Installer](https://apps.microsoft.com/detail/9nblggh4nns1) из Microsoft Store |
| Spotify, версия с сайта, не Microsoft Store | `winget install --id Spotify.Spotify --exact --source winget` или [spotify.com/download/windows](https://www.spotify.com/download/windows/) |
| Spicetify CLI | `winget install --id Spicetify.Spicetify --exact --source winget` или [официальная инструкция](https://spicetify.app/docs/getting-started) |
| Windhawk, по желанию | `winget install --id RamenSoftware.Windhawk --exact --source winget` или [windhawk.net](https://windhawk.net/) |

Интернет нужен для установки компонентов и проверки обновлений Spicetify. Сам редактор и предпросмотр работают локально. Marketplace и другие расширения не требуются.

Параметры установки: `-SkipDependencies` — компоненты уже стоят; `-NoAutoRepair` — не включать фоновые проверки; `-NoLaunch` — не открывать приложение после установки. Запуск приложения из распакованной папки без Install.cmd тоже поддерживается; для фоновой защиты используйте постоянную папку, которую не будете перемещать.

## Редактор и свои темы

Выберите пресет, измените HEX-цвета или нажмите круг рядом с цветом для выбора из диалога. Назовите тему, установите скругления и сохраните её. Изменения до нажатия **Применить** касаются только предпросмотра.

Темы хранятся в `%LOCALAPPDATA%\SpicetifyGuard\themes`. Экспортированный JSON содержит палитру и дополнительные настройки, а также ваш CSS. Формат показан в `themes/*.json`: `FormatVersion: 1`, уникальный `Id`, `Name`, все 15 значений `Colors`, `Radius` от 0 до 24, `Compact`, `HideBranding` и `CustomCss`.

Применяемые файлы находятся в `%APPDATA%\spicetify\Themes\GuardStudio`. Редактор сохраняет расширения и custom apps из конфигурации; для собственной темы выключает theme.js. Перед применением исходный конфиг и файлы предыдущей GuardStudio копируются в `%LOCALAPPDATA%\SpicetifyGuard\backups`. Ваша прежняя папка ZiroNeutral не удаляется.

## Защита, восстановление и удаление

Во вкладке **Защита и установка** можно включить/отключить задачу `SpicetifyGuard Auto Repair`, запустить ремонт и открыть журнал. Лог: `%LOCALAPPDATA%\SpicetifyGuard\guard.log`; установка: `install.log` рядом.

Если Spotify выпустил несовместимое обновление раньше Spicetify, восстановление может завершиться ошибкой. Guard сообщает её и запускает клиент снова; обновите Spicetify или дождитесь совместимой версии. Не копируйте резервные Apps от старой сборки поверх новой. Для Microsoft Store Spotify установите обычный desktop-клиент.

Вернуть обычный Spotify: отключите фоновую защиту, затем выполните `spicetify restore`. Чтобы вернуться к прежней ZiroNeutral: `spicetify config current_theme ZiroNeutral color_scheme neutral-light`, затем `spicetify apply`.

Удаление Guard: запустите `scripts/uninstall.ps1` из установленной папки через PowerShell. Скрипт удаляет только приложение, его ярлыки и принадлежащую ему задачу. Spotify, Spicetify, Windhawk, пользовательские темы, конфиги и резервные копии сохраняются.

## Сборка и проверка

Нет NuGet-зависимостей и отдельного .NET SDK: используется штатный компилятор .NET Framework и WPF.

```powershell
.\build.ps1 -Test
```

Готовое приложение: `dist/SpicetifyGuard/SpicetifyGuard.exe`. Архив: `dist/SpicetifyGuard-Studio-win-x64.zip`.

Тесты проверяют пресеты и контраст, импорт/экспорт, валидацию, рендеринг CSS/INI и обнаружение устаревших/отсутствующих файлов патча. Проверки выполняются в изолированной временной папке и не меняют установленный Spotify.

Шаблон GitHub Actions находится в `ci/windows-build.yml`. Чтобы включить автоматическую сборку, перенесите его в `.github/workflows/build.yml` через учётную запись с разрешением `workflow`. Публикуемый архив собран и проверен локально.

CLI: `--status` выводит JSON состояния; `--repair-silent` ремонтирует только при обнаруженной проблеме; `--repair-now` выполняет принудительный ремонт; `--enable-auto`/`--disable-auto` управляют задачей; `--render-theme input.json output-directory` создаёт Spicetify-файлы без применения.

Основа оформления — [Ziro](https://github.com/spicetify/spicetify-themes/tree/master/Ziro) с нашими поправками. Лицензии — [MIT](LICENSE) и [уведомления о сторонних компонентах](THIRD-PARTY-NOTICES.md).
