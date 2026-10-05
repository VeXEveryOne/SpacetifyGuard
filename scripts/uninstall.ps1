$ErrorActionPreference = 'Stop'
$taskInstallDir = [IO.Path]::GetFullPath((Join-Path $env:LOCALAPPDATA 'Programs\SpicetifyGuardStudio'))
$taskExpectedDir = [IO.Path]::GetFullPath((Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'Programs\SpicetifyGuardStudio'))
if ($taskInstallDir -ne $taskExpectedDir) { throw 'Unexpected install directory.' }
$taskExe = Join-Path $taskInstallDir 'SpicetifyGuard.exe'
$taskEntry = Get-ScheduledTask -TaskName 'SpicetifyGuard Auto Repair' -ErrorAction SilentlyContinue
if ($taskEntry -and ($taskEntry.Actions.Execute -contains $taskExe)) { Unregister-ScheduledTask -TaskName 'SpicetifyGuard Auto Repair' -Confirm:$false }
foreach ($taskProcess in (Get-Process SpicetifyGuard -ErrorAction SilentlyContinue)) {
    if ($taskProcess.Path -eq $taskExe) { $taskProcess.Kill(); $taskProcess.WaitForExit(3000) | Out-Null }
}
foreach ($taskShortcutFolder in @([Environment]::GetFolderPath('Desktop'),[Environment]::GetFolderPath('Programs'))) {
    $taskLink = Join-Path $taskShortcutFolder 'Spicetify Guard Studio.lnk'
    if (Test-Path -LiteralPath $taskLink) { Remove-Item -LiteralPath $taskLink }
}
if (Test-Path -LiteralPath $taskInstallDir) { Remove-Item -LiteralPath $taskInstallDir -Recurse }
Write-Output 'Guard Studio uninstalled. Spotify, Spicetify, Windhawk, custom themes and backups were preserved.'
