param(
    [switch]$WithWindhawk,
    [switch]$DependenciesOnly,
    [switch]$SkipDependencies,
    [switch]$NoAutoRepair,
    [switch]$NoLaunch
)
$ErrorActionPreference = 'Stop'
$taskAppSource = Split-Path -Parent $PSScriptRoot
$taskInstallDir = Join-Path $env:LOCALAPPDATA 'Programs\SpicetifyGuardStudio'
$taskDataDir = Join-Path $env:LOCALAPPDATA 'SpicetifyGuard'
New-Item -ItemType Directory -Path $taskDataDir -Force | Out-Null
$taskLog = Join-Path $taskDataDir 'install.log'
function Write-InstallLog([string]$message) {
    Write-Output $message
    Add-Content -LiteralPath $taskLog -Value "$(Get-Date -Format 'yyyy-MM-dd HH:mm:ss') $message" -Encoding UTF8
}
function Install-Package([string]$id, [switch]$UserScope) {
    if (-not (Get-Command winget.exe -ErrorAction SilentlyContinue)) {
        throw 'Windows Package Manager (winget) is missing. Install App Installer: https://apps.microsoft.com/detail/9nblggh4nns1'
    }
    Write-InstallLog "Installing/checking $id..."
    $taskArguments = @('install','--id',$id,'--exact','--source','winget','--accept-source-agreements','--accept-package-agreements','--disable-interactivity')
    if ($UserScope) { $taskArguments += @('--scope','user') }
    $taskOutput = & winget.exe @taskArguments 2>&1
    $taskCode = $LASTEXITCODE
    $taskOutput | Out-File -LiteralPath $taskLog -Append -Encoding UTF8
    # APPINSTALLER_CLI_ERROR_UPDATE_NOT_APPLICABLE: already installed, current version.
    if ($taskCode -ne 0 -and $taskCode -ne -1978335189) { throw "Installation of $id failed ($taskCode). See $taskLog" }
}
if (-not $SkipDependencies) {
    if (-not (Test-Path -LiteralPath (Join-Path $env:APPDATA 'Spotify\Spotify.exe'))) { Install-Package 'Spotify.Spotify' -UserScope }
    $taskSpicetify = Join-Path $env:LOCALAPPDATA 'spicetify\spicetify.exe'
    if (-not (Test-Path -LiteralPath $taskSpicetify) -and -not (Get-Command spicetify.exe -ErrorAction SilentlyContinue)) { Install-Package 'Spicetify.Spicetify' }
    if ($WithWindhawk -and -not (Test-Path -LiteralPath (Join-Path $env:ProgramFiles 'Windhawk\windhawk.exe'))) { Install-Package 'RamenSoftware.Windhawk' }
    $taskPath = [Environment]::GetEnvironmentVariable('PATH','Machine') + ';' + [Environment]::GetEnvironmentVariable('PATH','User')
    $env:PATH = $taskPath
    Write-InstallLog 'Dependencies installed. Open Spotify and sign in before the first theme apply.'
    # Initialize Spicetify's config after dependencies are present. It can fail
    # until Spotify has completed its first launch; this is shown in the app.
    $taskCli = Get-Command spicetify.exe -ErrorAction SilentlyContinue
    if (Test-Path -LiteralPath $taskSpicetify) { $taskCliPath = $taskSpicetify } elseif ($taskCli) { $taskCliPath = $taskCli.Source } else { $taskCliPath = $null }
    if ($taskCliPath) {
        $taskCliArgs = @()
        if ((Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System').EnableLUA -eq 0) { $taskCliArgs += '--bypass-admin' }
        if (-not (Test-Path -LiteralPath (Join-Path $env:APPDATA 'spicetify\config-xpui.ini'))) {
            & $taskCliPath @taskCliArgs 2>&1 | Out-File -LiteralPath $taskLog -Append -Encoding UTF8
        }
        & $taskCliPath @taskCliArgs upgrade 2>&1 | Out-File -LiteralPath $taskLog -Append -Encoding UTF8
        if ($LASTEXITCODE -ne 0) { Write-InstallLog 'Spicetify upgrade did not complete. Use Repair in the app if required.' }
    }
}
if ($DependenciesOnly) { exit 0 }
$taskSourceExe = Join-Path $taskAppSource 'SpicetifyGuard.exe'
if (-not (Test-Path -LiteralPath $taskSourceExe)) { throw 'Run Install.cmd from the extracted release archive, or run build.ps1 first.' }
foreach ($taskProcess in (Get-Process SpicetifyGuard -ErrorAction SilentlyContinue)) {
    $taskProcess.CloseMainWindow() | Out-Null
    if (-not $taskProcess.WaitForExit(3000)) { $taskProcess.Kill(); $taskProcess.WaitForExit(3000) | Out-Null }
}
New-Item -ItemType Directory -Path $taskInstallDir -Force | Out-Null
if ([IO.Path]::GetFullPath($taskAppSource) -ne [IO.Path]::GetFullPath($taskInstallDir)) {
    Get-ChildItem -LiteralPath $taskAppSource | Copy-Item -Destination $taskInstallDir -Recurse -Force
}
$taskInstalledExe = Join-Path $taskInstallDir 'SpicetifyGuard.exe'
$taskShell = New-Object -ComObject WScript.Shell
foreach ($taskShortcutFolder in @([Environment]::GetFolderPath('Desktop'),[Environment]::GetFolderPath('Programs'))) {
    $taskShortcut = $taskShell.CreateShortcut((Join-Path $taskShortcutFolder 'Spicetify Guard Studio.lnk'))
    $taskShortcut.TargetPath = $taskInstalledExe
    $taskShortcut.WorkingDirectory = $taskInstallDir
    $taskShortcut.Save()
}
if (-not $NoAutoRepair) {
    $taskEnable = Start-Process -FilePath $taskInstalledExe -ArgumentList '--enable-auto' -WindowStyle Hidden -PassThru -Wait
    if ($taskEnable.ExitCode -ne 0) { throw "Could not enable background repair ($($taskEnable.ExitCode))." }
}
Write-InstallLog "Installed Guard Studio in $taskInstallDir. Custom themes and logs are preserved in $taskDataDir."
if (-not $NoLaunch) { Start-Process -FilePath $taskInstalledExe -WindowStyle Hidden }
