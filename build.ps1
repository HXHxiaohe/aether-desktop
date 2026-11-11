# Aether Desktop build script
#   .\build.ps1            -> Release build into src\Aether\bin\Release\<tfm>
#   .\build.ps1 -Publish   -> framework-dependent package into dist\Aether
#   .\build.ps1 -Install   -> publish, then install/update %LOCALAPPDATA%\Programs\Aether Desktop
#                             (keeps its Data folder), Start-menu shortcuts, autostart, and start it
#   .\build.ps1 -Release -Repo owner/name [-Version 1.2.3]
#                          -> dist\Aether-<version>.zip for a GitHub Release (friends unzip and
#                             run Aether.exe; it installs itself and updates from that repo)
param([switch]$Publish, [switch]$Install, [switch]$Release, [string]$Repo = $env:AETHER_REPO, [string]$Version = '')
if ($Install -or $Release) { $Publish = $true }
# Remember the update repo locally so -Install builds keep updating from it.
$repoFile = Join-Path $PSScriptRoot 'repo.txt'
if (-not $Repo -and (Test-Path $repoFile)) { $Repo = (Get-Content $repoFile -TotalCount 1).Trim() }
if ($Release -and -not $Repo) { throw 'Release needs -Repo owner/name' }

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot

# Prefer a system SDK; else $env:AETHER_SDK or the path in sdk.path (git-ignored, local only).
$dotnet = 'dotnet'
$sdks = & dotnet --list-sdks 2>$null
if (-not $sdks) {
    $fallback = $env:AETHER_SDK
    if (-not $fallback -and (Test-Path (Join-Path $root 'sdk.path'))) { $fallback = (Get-Content (Join-Path $root 'sdk.path') -TotalCount 1).Trim() }
    if (-not $fallback -or -not (Test-Path $fallback)) { throw '.NET 10 SDK not found (install it, or put the path to dotnet.exe in sdk.path)' }
    $dotnet = $fallback
    $env:DOTNET_ROOT = Split-Path $fallback
}
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'

$proj = Join-Path $root 'src\Aether\Aether.csproj'

$installDir = Join-Path $env:LOCALAPPDATA 'Programs\Aether Desktop'

function Stop-Aether([string[]]$dirs) {
    # Graceful quit (restores the taskbar), then make sure.
    $running = @(Get-Process Aether -ErrorAction SilentlyContinue | Where-Object { $p = $_.Path; $p -and ($dirs | Where-Object { $p.StartsWith($_, 'OrdinalIgnoreCase') }) })
    if ($running.Count -eq 0) { return }
    try {
        $quit = [System.Threading.EventWaitHandle]::OpenExisting('Local\Aether.Desktop.v1.Quit')
        [void]$quit.Set()
        $quit.Dispose()
    } catch { }
    foreach ($p in $running) {
        if (-not $p.WaitForExit(5000)) { Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue }
    }
}

# Stop a running dev copy so its exe can be replaced.
$binDir = Join-Path $root 'src\Aether\bin'
Stop-Aether @($binDir)

if ($Publish) {
    $out = Join-Path $root 'dist\Aether'
    if (Test-Path $out) { Remove-Item $out -Recurse -Force }
    $props = @("-p:UpdateRepo=$Repo")
    if ($Version) { $props += "-p:Version=$Version" }
    & $dotnet publish $proj -c Release -o $out --self-contained false -p:DebugType=none @props
} else {
    & $dotnet build $proj -c Release
}
if ($LASTEXITCODE -ne 0) { throw "build failed ($LASTEXITCODE)" }

# Safety net (no git on this machine): zip the source after every good build, keep the last 20.
$backups = Join-Path $root 'backups'
New-Item -ItemType Directory -Force $backups | Out-Null
$zip = Join-Path $backups ("src-{0:yyyyMMdd-HHmmss}.zip" -f (Get-Date))
$files = Get-ChildItem (Join-Path $root 'src') -Recurse -File | Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' }
Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [System.IO.Compression.ZipFile]::Open($zip, 'Create')
try {
    foreach ($f in $files) {
        $rel = $f.FullName.Substring($root.Length + 1)
        [void][System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $f.FullName, $rel)
    }
} finally { $archive.Dispose() }
Get-ChildItem $backups -Filter 'src-*.zip' | Sort-Object Name -Descending | Select-Object -Skip 20 | Remove-Item -Force

if ($Release) {
    $ver = (Get-Item (Join-Path $root 'dist\Aether\Aether.dll')).VersionInfo.ProductVersion -replace '\+.*$', ''
    $zipOut = Join-Path $root "dist\Aether-$ver.zip"
    if (Test-Path $zipOut) { Remove-Item $zipOut -Force }
    [System.IO.Compression.ZipFile]::CreateFromDirectory((Join-Path $root 'dist\Aether'), $zipOut)
    Write-Host "release package: $zipOut  (tag it v$ver)"
    return
}
if (-not $Install) { return }

# ---------------- install / update ----------------
$dist = Join-Path $root 'dist\Aether'
Stop-Aether @($installDir, $binDir)
New-Item -ItemType Directory -Force $installDir | Out-Null
# Mirror program files, never touch the user's Data folder.
& robocopy $dist $installDir /MIR /XD Data /NFL /NDL /NJH /NJS /NP | Out-Null
if ($LASTEXITCODE -ge 8) { throw "copy failed (robocopy $LASTEXITCODE)" }

# First install: bring settings, notes, lyrics and icon cache over from the dev copy.
$dataTarget = Join-Path $installDir 'Data'
$devData = Join-Path $root 'src\Aether\bin\Release\net10.0-windows\Data'
if (-not (Test-Path (Join-Path $dataTarget 'settings.json')) -and (Test-Path $devData)) {
    & robocopy $devData $dataTarget /E /XF error.log* *.tmp /NFL /NDL /NJH /NJS /NP | Out-Null
    Write-Host "copied settings from the dev build"
}

# Start-menu shortcuts.
$exe = Join-Path $installDir 'Aether.exe'
$menu = Join-Path ([Environment]::GetFolderPath('Programs')) 'Aether Desktop'
New-Item -ItemType Directory -Force $menu | Out-Null
$shell = New-Object -ComObject WScript.Shell
$lnk = $shell.CreateShortcut((Join-Path $menu 'Aether Desktop.lnk'))
$lnk.TargetPath = $exe; $lnk.WorkingDirectory = $installDir; $lnk.Description = 'Aether Desktop'; $lnk.Save()
$lnk = $shell.CreateShortcut((Join-Path $menu 'Aether 恢复任务栏（应急）.lnk'))
$lnk.TargetPath = $exe; $lnk.Arguments = '--restore-taskbar'; $lnk.WorkingDirectory = $installDir
$lnk.Description = '把 Windows 任务栏恢复原样并关掉隐藏任务栏选项'; $lnk.Save()

# Autostart: keep the level chosen in settings; first install gets the "high" logon task.
cmd.exe /c 'schtasks /Query /TN "Aether Desktop" >nul 2>nul'
$hasTask = $LASTEXITCODE -eq 0
$hasRun = $null -ne (Get-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name 'AetherDesktop' -ErrorAction SilentlyContinue)
if (-not $hasTask -and -not $hasRun) { Start-Process $exe -ArgumentList '--autostart-mode','high' -Wait }

# Start through Explorer so Aether (and every app opened from its Dock) isn't a child of
# whatever ran this script — closing that terminal/agent must not take them down.
Start-Process explorer.exe -ArgumentList "`"$exe`""
Write-Host "installed to $installDir"
exit 0
