# Publish a release to GitHub.
#   .\publish.ps1 -Version 1.0.1 -Notes "改了什么"     -> new release (friends get the update)
#   .\publish.ps1 -Version 1.0.0 -Replace              -> rebuild and replace that release's zip
# Copies the committed source (git archive HEAD) into ..\Aether-public, commits it there with the
# GitHub noreply identity, pushes, builds dist\Aether-<ver>.zip and uploads it.
param([Parameter(Mandatory)][string]$Version, [string]$Notes = '', [switch]$Replace)
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$pub = Join-Path (Split-Path $root) 'Aether-public'
$git = 'C:\Program Files\Git\cmd\git.exe'
$gh = 'C:\Program Files\GitHub CLI\gh.exe'
$repo = (Get-Content (Join-Path $root 'repo.txt') -TotalCount 1).Trim()
if (& $git -C $root status --porcelain) { throw 'commit your changes first (publish uses the last commit)' }

# 1. source -> public repo
$zip = Join-Path $env:TEMP 'aether-src.zip'
& $git -C $root archive --format=zip -o $zip HEAD
Get-ChildItem $pub -Force | Where-Object { $_.Name -ne '.git' } | Remove-Item -Recurse -Force
Expand-Archive $zip $pub; Remove-Item $zip
& $git -C $pub add -A
if (& $git -C $pub status --porcelain) {
    & $git -C $pub commit -q -m ("Aether Desktop $Version" + $(if ($Notes) { "`n`n$Notes" } else { '' }))
    & $git -C $pub push -q
}

# 2. build the zip with the update repo baked in
& (Join-Path $root 'build.ps1') -Release -Repo $repo -Version $Version
$asset = Join-Path $root "dist\Aether-$Version.zip"
if (-not (Test-Path $asset)) { throw "build did not produce $asset" }

# 3. upload
if ($Replace) {
    & $gh release upload "v$Version" $asset --clobber -R $repo
} else {
    $body = if ($Notes) { $Notes } else { "Aether Desktop $Version" }
    $body += "`n`n**安装**：下载 ``Aether-$Version.zip``，解压后双击 ``Aether.exe``。已安装的会在 设置 › 关于 里提示更新。"
    & $gh release create "v$Version" $asset --title "Aether Desktop $Version" --notes $body -R $repo
}
Write-Host "published v$Version -> https://github.com/$repo/releases/tag/v$Version"
