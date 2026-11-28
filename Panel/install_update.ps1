param([switch]$NoPause)

# CS2 Bot Improver - one-click plugin update (standalone helper).
# Run with: powershell.exe -NoProfile -ExecutionPolicy Bypass -File "Panel\install_update.ps1"

$ErrorActionPreference = 'Stop'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

$ApiUrl = 'https://api.github.com/repos/ed0ard/CS2-Bot-Improver/releases/latest'
$Headers = @{
    'User-Agent' = 'CS2-Bot-Improver-Panel'
    'Accept'     = 'application/vnd.github+json'
}

function Write-Step { param([string]$Message) Write-Host "[CS2BI] $Message" -ForegroundColor Cyan }
function Write-Ok   { param([string]$Message) Write-Host "[CS2BI] $Message" -ForegroundColor Green }
function Write-Note { param([string]$Message) Write-Host "[CS2BI] $Message" -ForegroundColor Yellow }

function Get-SteamRoots {
    $roots = New-Object 'System.Collections.Generic.List[string]'

    foreach ($key in @(
        'HKCU:\Software\Valve\Steam',
        'HKLM:\SOFTWARE\WOW6432Node\Valve\Steam',
        'HKLM:\SOFTWARE\Valve\Steam'
    )) {
        if (-not (Test-Path -LiteralPath $key)) { continue }
        $props = Get-ItemProperty -LiteralPath $key -ErrorAction SilentlyContinue
        foreach ($name in @('SteamPath', 'InstallPath')) {
            $value = $props.$name
            if ($value -and (Test-Path -LiteralPath $value)) { $roots.Add($value) }
        }
    }

    foreach ($value in @(
        $env:ProgramFiles,
        ${env:ProgramFiles(x86)},
        'D:\Steam',
        'C:\Program Files\Steam',
        'C:\Program Files (x86)\Steam'
    )) {
        if ($value -and (Test-Path -LiteralPath (Join-Path $value 'steamapps'))) {
            $roots.Add($value)
        }
    }

    # Steam can store games on other drives; read libraryfolders.vdf.
    foreach ($root in @($roots)) {
        $vdf = Join-Path $root 'steamapps\libraryfolders.vdf'
        if (-not (Test-Path -LiteralPath $vdf)) { continue }
        $content = Get-Content -LiteralPath $vdf -Raw
        foreach ($match in [regex]::Matches($content, '"path"\s+"([^"]+)"')) {
            $lib = $match.Groups[1].Value -replace '\\\\', '\'
            if ($lib -and (Test-Path -LiteralPath (Join-Path $lib 'steamapps'))) {
                $roots.Add($lib)
            }
        }
    }

    return @($roots | Select-Object -Unique)
}

function Find-CsgoDir {
    $relatives = @(
        'steamapps\common\Counter-Strike Global Offensive\game\csgo',
        'steamapps\common\Counter-Strike Global Offensive\csgo'
    )
    foreach ($root in (Get-SteamRoots)) {
        foreach ($rel in $relatives) {
            $candidate = Join-Path $root $rel
            if (Test-Path -LiteralPath $candidate) { return $candidate }
        }
    }
    return $null
}

function Copy-Tree {
    param([string]$Source, [string]$Destination)

    New-Item -ItemType Directory -Path $Destination -Force | Out-Null
    foreach ($item in (Get-ChildItem -LiteralPath $Source -Force)) {
        $to = Join-Path $Destination $item.Name
        if ($item.PSIsContainer) {
            Copy-Tree -Source $item.FullName -Destination $to
        } else {
            Copy-Item -LiteralPath $item.FullName -Destination $to -Force
        }
    }
}

# 1. Locate game/csgo.
$target = Find-CsgoDir
if (-not $target) {
    Write-Note 'game/csgo was not found automatically.'
    $target = (Read-Host 'Enter the full path to game/csgo').Trim([char]'"')
}
if (-not (Test-Path -LiteralPath $target)) { throw "Target directory does not exist: $target" }
Write-Step "Target: $target"

if (Get-Process -Name 'cs2' -ErrorAction SilentlyContinue) {
    Write-Note 'CS2 is running. Overwriting files may fail; close CS2 and retry if needed.'
}

# 2. Latest release metadata and the standard Windows zip.
$release = Invoke-RestMethod -Uri $ApiUrl -Headers $Headers
$asset = $release.assets | Where-Object { $_.name -eq 'CS2BotImprover.zip' } | Select-Object -First 1
if (-not $asset) {
    $asset = $release.assets | Where-Object {
        $_.name -like '*.zip' -and $_.name -notlike '*Linux*' -and $_.name -notlike '*unchanged*'
    } | Select-Object -First 1
}
if (-not $asset) { throw 'No usable Windows zip asset found in the latest release.' }
Write-Step "Release: $($release.tag_name) / $($asset.name)"

# 3. Download + extract into a unique temp folder.
$tempRoot = Join-Path $env:TEMP ("cs2bi-update-" + [guid]::NewGuid().ToString('N'))
$zipPath = Join-Path $tempRoot 'update.zip'
$extractDir = Join-Path $tempRoot 'extracted'
New-Item -ItemType Directory -Path $tempRoot -Force | Out-Null

try {
    Write-Step 'Downloading update package...'
    Invoke-WebRequest -Uri $asset.browser_download_url -OutFile $zipPath -Headers $Headers

    Write-Step 'Extracting...'
    Expand-Archive -LiteralPath $zipPath -DestinationPath $extractDir -Force

    # Archives may wrap everything in a single root folder.
    $addonsSrc = Get-ChildItem -LiteralPath $extractDir -Recurse -Directory -Filter 'addons' -ErrorAction SilentlyContinue |
        Sort-Object { $_.FullName.Length } | Select-Object -First 1
    $giSrc = Get-ChildItem -LiteralPath $extractDir -Recurse -File -Filter 'gameinfo.gi' -ErrorAction SilentlyContinue |
        Sort-Object { $_.FullName.Length } | Select-Object -First 1
    if (-not $addonsSrc -or -not $giSrc) {
        throw 'Archive is missing addons/ or gameinfo.gi.'
    }

    # 4. Backup before overwrite.
    $giDst = Join-Path $target 'gameinfo.gi'
    $bakPath = Join-Path $target 'gameinfo.gi.bak'
    $hadGi = Test-Path -LiteralPath $giDst
    if ($hadGi) {
        if (Test-Path -LiteralPath $bakPath) { Remove-Item -LiteralPath $bakPath -Force }
        Move-Item -LiteralPath $giDst -Destination $bakPath -Force
        Write-Step "Backup: $bakPath"
    }

    # 5. Merge addons and copy gameinfo.gi; restore the backup on failure.
    try {
        Copy-Tree -Source $addonsSrc.FullName -Destination (Join-Path $target 'addons')
        Copy-Item -LiteralPath $giSrc.FullName -Destination $giDst -Force
    } catch {
        if ($hadGi -and (Test-Path -LiteralPath $bakPath)) {
            Remove-Item -LiteralPath $giDst -Force -ErrorAction SilentlyContinue
            Move-Item -LiteralPath $bakPath -Destination $giDst -Force -ErrorAction SilentlyContinue
        }
        throw
    }

    Write-Ok "Update complete: $($release.tag_name)"
} finally {
    # 6. Cleanup temp files even on failure.
    if (Test-Path -LiteralPath $tempRoot) {
        Remove-Item -LiteralPath $tempRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}

if (-not $NoPause -and [Environment]::UserInteractive) {
    Read-Host 'Press Enter to exit' | Out-Null
}
