[CmdletBinding()]
param(
    [string]$DotNet,
    [string]$Cargo,
    [string]$Rustc,
    [string]$RustToolchain,
    [string]$LlvmBin,
    [string]$XwinCache,
    [string]$OutputDirectory,
    [string]$ReleaseVersion = "1.4.2.6-Preview.2",
    [string]$MinimumPanelVersion = "1.4.2.4",
    [switch]$SkipBuild,
    [switch]$SkipNpmInstall
)

$ErrorActionPreference = "Stop"
$repo = Split-Path -Parent $PSScriptRoot
$displayVersion = $ReleaseVersion.Trim().TrimStart('v', 'V')
if ($displayVersion -notmatch '^\d+\.\d+\.\d+\.\d+(?:-Preview\.\d+)?$') {
    throw "ReleaseVersion must use four numeric parts with an optional -Preview.N suffix."
}
$minimumPanelVersion = $MinimumPanelVersion.Trim().TrimStart('v', 'V')
if ($minimumPanelVersion -notmatch '^\d+\.\d+\.\d+\.\d+(?:-Preview\.\d+)?$') {
    throw "MinimumPanelVersion must use four numeric parts with an optional -Preview.N suffix."
}
$isPreview = $displayVersion -match '-Preview\.\d+$'
$releaseTag = "v$displayVersion"
$manifest = Get-Content -LiteralPath (Join-Path $PSScriptRoot "dependencies.json") -Raw | ConvertFrom-Json
. (Join-Path $PSScriptRoot "VpkTools.ps1")
$cache = Join-Path $repo ".cache\package"
$stage = Join-Path $cache "stage"
$extract = Join-Path $cache "extract"
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $repo "artifacts" }

function Get-VerifiedAsset {
    param($Asset)
    New-Item -ItemType Directory -Path $cache -Force | Out-Null
    $path = Join-Path $cache $Asset.name
    $expected = $Asset.sha256.ToLowerInvariant()
    if (Test-Path -LiteralPath $path) {
        $cached = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($cached -eq $expected) { return $path }
        Write-Host "Refreshing stale cached asset: $($Asset.name)"
    }

    $download = "$path.download"
    try {
        if (Test-Path -LiteralPath $download) { Remove-Item -LiteralPath $download -Force }
        Invoke-WebRequest -Uri $Asset.url -OutFile $download
        $actual = (Get-FileHash -LiteralPath $download -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($actual -ne $expected) {
            throw "SHA-256 mismatch for $($Asset.name): $actual"
        }
        Move-Item -LiteralPath $download -Destination $path -Force
    }
    finally {
        if (Test-Path -LiteralPath $download) { Remove-Item -LiteralPath $download -Force }
    }
    return $path
}

function Copy-Tree {
    param([string]$Source, [string]$Destination)
    New-Item -ItemType Directory -Path $Destination -Force | Out-Null
    Copy-Item -Path (Join-Path $Source "*") -Destination $Destination -Recurse -Force
}

function Expand-TarGz {
    param([string]$Archive, [string]$Destination)
    $tar = (Get-Command tar.exe -ErrorAction Stop).Source
    New-Item -ItemType Directory -Path $Destination -Force | Out-Null
    & $tar -xzf $Archive -C $Destination
    if ($LASTEXITCODE -ne 0) { throw "Failed to extract $Archive" }
}

function Assert-ChildPath {
    param([string]$Parent, [string]$Child)
    $parentPath = [IO.Path]::GetFullPath($Parent).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    $childPath = [IO.Path]::GetFullPath($Child)
    if (-not $childPath.StartsWith($parentPath, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to modify a path outside the package cache: $childPath"
    }
}

if (-not $SkipBuild) {
    $buildArguments = @{ SkipNpmInstall = $SkipNpmInstall }
    if ($DotNet) { $buildArguments.DotNet = $DotNet }
    if ($Cargo) { $buildArguments.Cargo = $Cargo }
    if ($Rustc) { $buildArguments.Rustc = $Rustc }
    if ($RustToolchain) { $buildArguments.RustToolchain = $RustToolchain }
    if ($LlvmBin) { $buildArguments.LlvmBin = $LlvmBin }
    if ($XwinCache) { $buildArguments.XwinCache = $XwinCache }
    & (Join-Path $PSScriptRoot "build.ps1") @buildArguments
    if ($LASTEXITCODE -ne 0) { throw "Build failed." }
}

$upstreamZip = Get-VerifiedAsset $manifest.upstream.windowsAsset
$metamodZip = Get-VerifiedAsset $manifest.metamod.windowsAsset
$counterStrikeSharpZip = Get-VerifiedAsset $manifest.counterStrikeSharp.windowsAsset
$rayTraceCssArchive = Get-VerifiedAsset $manifest.rayTrace.cssAsset
$rayTraceWindowsArchive = Get-VerifiedAsset $manifest.rayTrace.windowsAsset
$botHiderZip = Get-VerifiedAsset $manifest.botHider.windowsAsset

Assert-ChildPath $cache $stage
Assert-ChildPath $cache $extract
if (Test-Path -LiteralPath $stage) { Remove-Item -LiteralPath $stage -Recurse -Force }
if (Test-Path -LiteralPath $extract) { Remove-Item -LiteralPath $extract -Recurse -Force }
New-Item -ItemType Directory -Path $stage,$extract -Force | Out-Null

$upstreamExtract = Join-Path $extract "upstream"
$metamodExtract = Join-Path $extract "metamod"
$counterStrikeSharpExtract = Join-Path $extract "counterstrikesharp"
$rayTraceCssExtract = Join-Path $extract "raytrace-css"
$rayTraceWindowsExtract = Join-Path $extract "raytrace-windows"
$botHiderExtract = Join-Path $extract "bothider"
Expand-Archive -LiteralPath $upstreamZip -DestinationPath $upstreamExtract
Expand-Archive -LiteralPath $metamodZip -DestinationPath $metamodExtract
Expand-Archive -LiteralPath $counterStrikeSharpZip -DestinationPath $counterStrikeSharpExtract
Expand-TarGz $rayTraceCssArchive $rayTraceCssExtract
Expand-TarGz $rayTraceWindowsArchive $rayTraceWindowsExtract
Expand-Archive -LiteralPath $botHiderZip -DestinationPath $botHiderExtract

$payloadCandidates = @((Get-Item -LiteralPath $upstreamExtract)) +
    @(Get-ChildItem -LiteralPath $upstreamExtract -Directory -Recurse)
$upstreamPayload = $payloadCandidates |
    Where-Object { (Test-Path (Join-Path $_.FullName "addons")) -and (Test-Path (Join-Path $_.FullName "cfg")) } |
    Select-Object -First 1
if (-not $upstreamPayload) { throw "Could not locate the upstream game/csgo payload." }

$releaseRoot = Join-Path $stage "LocalArena-$releaseTag-windows"
$payload = $releaseRoot
Copy-Tree $upstreamPayload.FullName $releaseRoot
Get-ChildItem -LiteralPath $releaseRoot -Filter "Panel*.exe" -File | Remove-Item -Force

# Never distribute gameinfo.gi from an older upstream release, including the
# manual Online/WithBots backups. The Plus Panel derives both variants from the
# installed game's current Steam-verified file when the user switches mode.
Get-ChildItem -LiteralPath $releaseRoot -Recurse -Filter "gameinfo.gi" -File |
    ForEach-Object {
        Assert-ChildPath $releaseRoot $_.FullName
        Remove-Item -LiteralPath $_.FullName -Force
    }

# Upstream botprofile VPKs also contain stale localization files. Current CS2
# loads those files ahead of its own resources, breaking player-name formatting.
# Keep only BotProfile.db; difficulty behavior remains byte-for-byte unchanged.
$botProfileVpks = @(Get-ChildItem -LiteralPath (Join-Path $payload "overrides") `
    -Filter "botprofile.vpk" -File -Recurse)
if ($botProfileVpks.Count -eq 0) {
    throw "The upstream payload contains no botprofile VPKs."
}
foreach ($botProfileVpk in $botProfileVpks) {
    ConvertTo-BotProfileOnlyVpk $botProfileVpk.FullName
}

$metamodAddons = Join-Path $metamodExtract "addons"
if (-not (Test-Path -LiteralPath $metamodAddons)) { throw "Metamod archive has no addons payload." }
Copy-Tree $metamodAddons (Join-Path $payload "addons")

$counterStrikeSharpAddons = Join-Path $counterStrikeSharpExtract "addons"
if (-not (Test-Path -LiteralPath $counterStrikeSharpAddons)) { throw "CounterStrikeSharp archive has no addons payload." }
Copy-Tree $counterStrikeSharpAddons (Join-Path $payload "addons")

$rayTraceCssRoot = Get-ChildItem -LiteralPath $rayTraceCssExtract -Directory -Recurse |
    Where-Object { Test-Path (Join-Path $_.FullName "counterstrikesharp\plugins\RayTraceImpl") } |
    Select-Object -First 1
if (-not $rayTraceCssRoot) { throw "Could not locate the RayTrace CounterStrikeSharp payload." }
Copy-Tree (Join-Path $rayTraceCssRoot.FullName "counterstrikesharp") (Join-Path $payload "addons\counterstrikesharp")

$rayTraceNativeCandidates = @((Get-Item -LiteralPath $rayTraceWindowsExtract)) +
    @(Get-ChildItem -LiteralPath $rayTraceWindowsExtract -Directory -Recurse)
$rayTraceNativeRoot = $rayTraceNativeCandidates |
    Where-Object { (Test-Path (Join-Path $_.FullName "RayTrace\bin\win64\RayTrace.dll")) -and
        (Test-Path (Join-Path $_.FullName "metamod\RayTrace.vdf")) } |
    Select-Object -First 1
if (-not $rayTraceNativeRoot) { throw "Could not locate the native RayTrace payload." }
Copy-Tree (Join-Path $rayTraceNativeRoot.FullName "RayTrace") (Join-Path $payload "addons\RayTrace")
Copy-Item -LiteralPath (Join-Path $rayTraceNativeRoot.FullName "metamod\RayTrace.vdf") `
    -Destination (Join-Path $payload "addons\metamod\RayTrace.vdf") -Force

$botHiderAddons = Get-ChildItem -LiteralPath $botHiderExtract -Directory -Recurse |
    Where-Object { $_.Name -eq "addons" -and (Test-Path (Join-Path $_.FullName "BotHider")) } |
    Select-Object -First 1
if (-not $botHiderAddons) { throw "Could not locate BotHider addons payload." }
Copy-Tree $botHiderAddons.FullName (Join-Path $payload "addons")
$linuxBotHiderVdf = Join-Path $payload "addons\metamod\BotHider.linux.vdf"
if (Test-Path -LiteralPath $linuxBotHiderVdf) {
    Remove-Item -LiteralPath $linuxBotHiderVdf -Force
}

# Plus configuration overlays. Source files are deliberately not copied into the release payload.
Copy-Item -LiteralPath (Join-Path $repo "addons\BotHider\bot_info.json") -Destination (Join-Path $payload "addons\BotHider\bot_info.json") -Force
Copy-Item -LiteralPath (Join-Path $repo "addons\BotHider\gamedata.json") -Destination (Join-Path $payload "addons\BotHider\gamedata.json") -Force
Copy-Item -LiteralPath (Join-Path $repo "addons\BotHider\map_whitelist.json") -Destination (Join-Path $payload "addons\BotHider\map_whitelist.json") -Force
Copy-Item -LiteralPath (Join-Path $repo "addons\metamod\BotHider.vdf") -Destination (Join-Path $payload "addons\metamod\BotHider.vdf") -Force
Copy-Item -LiteralPath (Join-Path $repo "cfg\my_bot_ffa_config.cfg") -Destination (Join-Path $payload "cfg\my_bot_ffa_config.cfg") -Force
Copy-Item -LiteralPath (Join-Path $repo "cfg\my_bot_normal_config.cfg") -Destination (Join-Path $payload "cfg\my_bot_normal_config.cfg") -Force

$pluginBuild = Join-Path $repo "addons\counterstrikesharp\plugins\PlayerKnifeCustomizer\bin\Release\net10.0"
$botImplBuild = Join-Path $repo "addons\counterstrikesharp\plugins\BotHiderImpl\bin\Release\net10.0"
$botApiBuild = Join-Path $repo "addons\counterstrikesharp\shared\BotHiderApi\bin\Release\net10.0"
$upstreamPluginBuilds = @(
    @{ Name = "BotAI"; Framework = "net10.0" },
    @{ Name = "BotAimImprover"; Framework = "net10.0" },
    @{ Name = "BotBuy"; Framework = "net8.0" },
    @{ Name = "BotControllerImpl"; Framework = "net10.0" },
    @{ Name = "BotRandomizer"; Framework = "net10.0" },
    @{ Name = "NadeSystem"; Framework = "net10.0" },
    @{ Name = "RoundDamageRecap"; Framework = "net10.0" }
    @{ Name = "PlusMatchCoordinator"; Framework = "net8.0" }
)
foreach ($plugin in $upstreamPluginBuilds) {
    $build = Join-Path $repo "addons\counterstrikesharp\plugins\$($plugin.Name)\bin\Release\$($plugin.Framework)"
    if (-not (Test-Path -LiteralPath (Join-Path $build "$($plugin.Name).dll"))) {
        throw "Expected upstream plugin build output was not produced: $build"
    }
    Copy-Tree $build (Join-Path $payload "addons\counterstrikesharp\plugins\$($plugin.Name)")
}
$botControllerApiBuild = Join-Path $repo "addons\counterstrikesharp\shared\BotControllerApi\bin\Release\net10.0"
if (-not (Test-Path -LiteralPath (Join-Path $botControllerApiBuild "BotControllerApi.dll"))) {
    throw "Expected BotController shared API build output was not produced: $botControllerApiBuild"
}
Copy-Tree $botControllerApiBuild (Join-Path $payload "addons\counterstrikesharp\shared\BotControllerApi")
Copy-Item -LiteralPath (Join-Path $repo "addons\counterstrikesharp\plugins\BotRandomizer\bot_randomizer_options.json") `
    -Destination (Join-Path $payload "addons\counterstrikesharp\plugins\BotRandomizer\bot_randomizer_options.json") -Force
Copy-Tree $pluginBuild (Join-Path $payload "addons\counterstrikesharp\plugins\PlayerKnifeCustomizer")
Copy-Tree $botImplBuild (Join-Path $payload "addons\counterstrikesharp\plugins\BotHiderImpl")
Copy-Tree $botApiBuild (Join-Path $payload "addons\counterstrikesharp\shared\BotHiderApi")
$openRatingModelPath = Join-Path $repo "addons\counterstrikesharp\shared\MatchCore\open-rating-3.0-proxy-v1.json"
$openRatingModel = Get-Content -LiteralPath $openRatingModelPath -Raw | ConvertFrom-Json
if (-not $openRatingModel.release_gate.passed) {
    throw "OpenRating calibration release gate has not passed; packaging an uncalibrated model is prohibited."
}
$openRatingCalibration = $openRatingModel.calibration
$openRatingGate = $openRatingModel.release_gate
if ([string]::IsNullOrWhiteSpace([string]$openRatingModel.dataset_sha256) -or
    [string]$openRatingModel.dataset_sha256 -notmatch '^[0-9a-f]{64}$') {
    throw "OpenRating calibration dataset fingerprint is missing or invalid."
}
if ([int]$openRatingCalibration.matches -lt [int]$openRatingGate.minimum_matches -or
    [int]$openRatingCalibration.maps -lt [int]$openRatingGate.minimum_maps -or
    [int]$openRatingCalibration.player_maps -lt [int]$openRatingGate.minimum_player_maps) {
    throw "OpenRating calibration sample does not satisfy its declared release gate."
}
if ([double]$openRatingCalibration.holdout_mae -gt [double]$openRatingGate.maximum_mae -or
    [double]$openRatingCalibration.holdout_spearman -lt [double]$openRatingGate.minimum_spearman -or
    [double]$openRatingGate.actual_holdout_fraction -lt [double]$openRatingGate.target_holdout_fraction) {
    throw "OpenRating holdout metrics do not satisfy the declared release gate."
}
$openRatingWeightNames = @('kills', 'damage', 'survival', 'kast', 'multi_kills', 'round_swing', 'economy')
foreach ($weightName in $openRatingWeightNames) {
    $weight = [double]$openRatingModel.weights.$weightName
    if (-not [double]::IsFinite($weight) -or $weight -lt 0) {
        throw "OpenRating weight '$weightName' must be finite and non-negative."
    }
}
if (-not [double]::IsFinite([double]$openRatingModel.weights.intercept)) {
    throw "OpenRating intercept must be finite."
}
Copy-Item -LiteralPath $openRatingModelPath -Destination (Join-Path $payload "addons\counterstrikesharp\plugins\PlusMatchCoordinator\open-rating-3.0-proxy-v1.json") -Force
$legacyRatingModel = Join-Path $payload "addons\counterstrikesharp\plugins\PlusMatchCoordinator\rating-plus-3.0-proxy-v1.json"
if (Test-Path -LiteralPath $legacyRatingModel) {
    Remove-Item -LiteralPath $legacyRatingModel -Force
}

# BotHiderImpl resolves Harmony from CounterStrikeSharp's shared library directory.
# The build target stages it below the plugin output so packaging can remain
# independent of the machine-wide NuGet cache.
$harmonyBuild = Join-Path $botImplBuild "shared\0Harmony\0Harmony.dll"
if (-not (Test-Path -LiteralPath $harmonyBuild)) {
    throw "Expected Harmony build output was not produced: $harmonyBuild"
}
$sharedHarmony = Join-Path $payload "addons\counterstrikesharp\shared\0Harmony"
New-Item -ItemType Directory -Path $sharedHarmony -Force | Out-Null
Copy-Item -LiteralPath $harmonyBuild -Destination (Join-Path $sharedHarmony "0Harmony.dll") -Force
$nestedShared = Join-Path $payload "addons\counterstrikesharp\plugins\BotHiderImpl\shared"
if (Test-Path -LiteralPath $nestedShared) {
    Assert-ChildPath $payload $nestedShared
    Remove-Item -LiteralPath $nestedShared -Recurse -Force
}

$nativeDll = Join-Path $payload "addons\BotHider\bin\win64\BotHider.dll"
$nativeHash = (Get-FileHash -LiteralPath $nativeDll -Algorithm SHA256).Hash.ToLowerInvariant()
if ($nativeHash -ne $manifest.botHider.windowsDllSha256.ToLowerInvariant()) {
    throw "Unexpected BotHider.dll SHA-256: $nativeHash"
}

$panelExe = Join-Path $repo "Panel\src-tauri\target\release\cs2-bot-improver-plus-panel.exe"
Copy-Item -LiteralPath $panelExe -Destination (Join-Path $releaseRoot "LocalArena.exe") -Force
$webViewLoader = Join-Path $repo "Panel\src-tauri\target\release\WebView2Loader.dll"
if (Test-Path -LiteralPath $webViewLoader) {
    Copy-Item -LiteralPath $webViewLoader -Destination (Join-Path $releaseRoot "WebView2Loader.dll") -Force
}
Copy-Item -LiteralPath (Join-Path $repo "README.md") -Destination (Join-Path $releaseRoot "README.md") -Force
Copy-Item -LiteralPath (Join-Path $repo "README.zh-CN.md") -Destination (Join-Path $releaseRoot "README.zh-CN.md") -Force
Copy-Item -LiteralPath (Join-Path $repo "LICENSE") -Destination (Join-Path $releaseRoot "LICENSE") -Force
if ($isPreview) {
    $packageReadme = Join-Path $releaseRoot "README.md"
    $packageReadmeZh = Join-Path $releaseRoot "README.zh-CN.md"
    (Get-Content -LiteralPath $packageReadme -Raw).Replace(
        "The current ``main`` branch targets **1.4.2.6**",
        "This local test package is **$displayVersion** (preview; may contain bugs; please report problems)"
    ) | Set-Content -LiteralPath $packageReadme -Encoding utf8
    (Get-Content -LiteralPath $packageReadmeZh -Raw).Replace(
        "当前 ``main`` 分支源码版本为 **1.4.2.6**",
        "当前本地测试包版本为 **$displayVersion**（预览版本，可能包含 Bug，请反馈）"
    ) | Set-Content -LiteralPath $packageReadmeZh -Encoding utf8
    @"
Local Arena $releaseTag

PREVIEW VERSION - MAY CONTAIN BUGS
This local test package is not an official GitHub release.
Please report problems together with an exported diagnostics ZIP.
"@ | Set-Content -LiteralPath (Join-Path $releaseRoot "PREVIEW-NOTICE.txt") -Encoding utf8
}

# The Panel uses this manifest as the installation ownership boundary. Only the
# game payload is managed; the executable and documentation stay portable.
$manifestEntries = foreach ($topLevel in @("addons", "cfg", "overrides")) {
    $root = Join-Path $payload $topLevel
    if (-not (Test-Path -LiteralPath $root)) { continue }
    foreach ($file in Get-ChildItem -LiteralPath $root -File -Recurse) {
        $relative = [IO.Path]::GetRelativePath($payload, $file.FullName).Replace("\", "/")
        $plusOwned = $relative -like "addons/counterstrikesharp/plugins/PlayerKnifeCustomizer/*" -or
            $relative -like "addons/counterstrikesharp/plugins/BotHiderImpl/*" -or
            $relative -like "addons/counterstrikesharp/plugins/PlusMatchCoordinator/*" -or
            $relative -like "addons/counterstrikesharp/shared/BotHiderApi/*" -or
            $relative -in @("cfg/my_bot_ffa_config.cfg", "cfg/my_bot_normal_config.cfg")
        $component = if ($relative -like "addons/counterstrikesharp/plugins/*") {
            ($relative -split "/")[3]
        }
        elseif ($relative -like "addons/BotHider/*") { "BotHider" }
        elseif ($relative -like "addons/RayTrace/*") { "RayTrace" }
        elseif ($relative -like "cfg/*") { "configuration" }
        elseif ($relative -like "overrides/*") { "overrides" }
        else { "runtime" }
        $preserveConfig = $relative -like "*/PlayerKnifeCustomizer/player_*_presets.json" -or
            $relative -in @(
                "addons/counterstrikesharp/plugins/BotRandomizer/bot_randomizer_options.json",
                "cfg/my_bot_ffa_config.cfg",
                "cfg/my_bot_normal_config.cfg",
                "overrides/botprofile.vpk"
            )
        [ordered]@{
            path = $relative
            size = $file.Length
            sha256 = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
            component = $component
            ownership = if ($plusOwned) { "plus" } else { "shared" }
            restore_policy = if ($preserveConfig) { "preserve-config" } else { "restore" }
        }
    }
}
$payloadManifest = [ordered]@{
    schema_version = 1
    package_version = $displayVersion
    entries = @($manifestEntries | Sort-Object path)
}
$payloadManifest | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $payload "plus-payload-manifest.json") -Encoding utf8

& (Join-Path $PSScriptRoot "verify-workspace.ps1") -PackageRoot $releaseRoot -ExpectedPackageVersion $displayVersion
if ($LASTEXITCODE -ne 0) { throw "Package verification failed." }

New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
Get-ChildItem -LiteralPath $OutputDirectory -File -ErrorAction SilentlyContinue |
    Where-Object { $_.Name -match '^(?:CS2BotImproverPlus|LocalArena)-.*\.zip$|^latest\.json(\.sig)?$|^SHA256SUMS\.txt$' } |
    Remove-Item -Force

$fullZip = Join-Path $OutputDirectory "LocalArena-$releaseTag-windows.zip"
Compress-Archive -Path $releaseRoot -DestinationPath $fullZip -CompressionLevel Optimal

$panelStage = Join-Path $stage "panel-update"
New-Item -ItemType Directory -Path $panelStage -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $releaseRoot "LocalArena.exe") -Destination $panelStage -Force
# Releases through 1.4.2.5 look up this legacy name before the new updater can run.
Copy-Item -LiteralPath (Join-Path $releaseRoot "LocalArena.exe") -Destination (Join-Path $panelStage "CS2BotImproverPlus.exe") -Force
@{
    schema_version = 1
    component = "panel-online-update"
    version = $displayVersion
    first_install_supported = $false
} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $panelStage "csbip-panel-update.json") -Encoding utf8
if (Test-Path -LiteralPath (Join-Path $releaseRoot "WebView2Loader.dll")) {
    Copy-Item -LiteralPath (Join-Path $releaseRoot "WebView2Loader.dll") -Destination $panelStage -Force
}
$panelZip = Join-Path $OutputDirectory "LocalArena-panel-$releaseTag-windows.zip"
Compress-Archive -Path (Join-Path $panelStage "*") -DestinationPath $panelZip -CompressionLevel Optimal

$pluginStage = Join-Path $stage "plugin-update"
New-Item -ItemType Directory -Path $pluginStage -Force | Out-Null
foreach ($name in @("addons", "cfg", "overrides", "plus-payload-manifest.json")) {
    $source = Join-Path $releaseRoot $name
    if (Test-Path -LiteralPath $source) { Copy-Item -LiteralPath $source -Destination $pluginStage -Recurse -Force }
}
$pluginZip = Join-Path $OutputDirectory "LocalArena-plugin-$releaseTag-windows.zip"
Compress-Archive -Path (Join-Path $pluginStage "*") -DestinationPath $pluginZip -CompressionLevel Optimal

$releaseBase = "https://github.com/numakkiyu/Local-Arena/releases/download/$releaseTag"
$latest = [ordered]@{
    schema_version = 1
    release_version = $displayVersion
    published_at = [DateTimeOffset]::UtcNow.ToString("o")
    release_notes_url = "https://github.com/numakkiyu/Local-Arena/releases/tag/$releaseTag"
    components = [ordered]@{
        panel = [ordered]@{
            version = $displayVersion
            url = "$releaseBase/$([IO.Path]::GetFileName($panelZip))"
            size = (Get-Item -LiteralPath $panelZip).Length
            sha256 = (Get-FileHash -LiteralPath $panelZip -Algorithm SHA256).Hash.ToLowerInvariant()
            min_panel_version = $minimumPanelVersion
        }
        plugin = [ordered]@{
            version = $displayVersion
            url = "$releaseBase/$([IO.Path]::GetFileName($pluginZip))"
            size = (Get-Item -LiteralPath $pluginZip).Length
            sha256 = (Get-FileHash -LiteralPath $pluginZip -Algorithm SHA256).Hash.ToLowerInvariant()
            min_panel_version = $minimumPanelVersion
        }
    }
}
$latestPath = Join-Path $OutputDirectory "latest.json"
$latest | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $latestPath -Encoding utf8
$signaturePath = Join-Path $OutputDirectory "latest.json.sig"
if ($env:CSBIP_UPDATE_SIGNING_KEY) {
    $python = (Get-Command python -ErrorAction Stop).Source
    & $python (Join-Path $PSScriptRoot "sign-update.py") $latestPath $signaturePath `
        --public-key (Join-Path $PSScriptRoot "update-public-key.txt")
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $signaturePath)) { throw "Update signing failed." }
}

$sumFiles = @($fullZip, $panelZip, $pluginZip, $latestPath)
if (Test-Path -LiteralPath $signaturePath) { $sumFiles += $signaturePath }
$sumLines = foreach ($file in $sumFiles) {
    "$((Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash.ToLowerInvariant())  $([IO.Path]::GetFileName($file))"
}
$sums = Join-Path $OutputDirectory "SHA256SUMS.txt"
Set-Content -LiteralPath $sums -Value $sumLines -Encoding ascii

Write-Host "Package complete: $fullZip"
Write-Host "Panel update: $panelZip"
Write-Host "Plugin update: $pluginZip"
