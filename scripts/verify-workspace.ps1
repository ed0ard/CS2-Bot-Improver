[CmdletBinding()]
param(
    [string]$PackageRoot,
    [string]$ExpectedPackageVersion = "1.4.2.6-Preview.2"
)

$ErrorActionPreference = "Stop"
$repo = Split-Path -Parent $PSScriptRoot
$manifestPath = Join-Path $PSScriptRoot "dependencies.json"
$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
. (Join-Path $PSScriptRoot "VpkTools.ps1")
$failures = [Collections.Generic.List[string]]::new()

function Add-Failure([string]$Message) {
    $failures.Add($Message)
}

function Assert-File([string]$Path, [string]$Label) {
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        Add-Failure "Missing $Label`: $Path"
    }
}

function Get-JsonCount([string]$RelativePath) {
    $path = Join-Path $repo $RelativePath
    try {
        $document = [System.Text.Json.JsonDocument]::Parse([IO.File]::ReadAllText($path))
        try {
            if ($document.RootElement.ValueKind -eq [System.Text.Json.JsonValueKind]::Array) {
                return $document.RootElement.GetArrayLength()
            }
            if ($document.RootElement.ValueKind -eq [System.Text.Json.JsonValueKind]::Object) {
                $count = 0
                foreach ($property in $document.RootElement.EnumerateObject()) { $count++ }
                return $count
            }
            return 1
        }
        finally {
            $document.Dispose()
        }
    }
    catch {
        Add-Failure "Invalid JSON $RelativePath`: $($_.Exception.Message)"
        return -1
    }
}

$base = if ($manifest.upstream.sourceCommit) { $manifest.upstream.sourceCommit } else { $manifest.upstream.baseCommit }

$localBuildConfig = Join-Path $repo ".local-build.ps1"
if ($env:GITHUB_ACTIONS -ne "true" -and -not (Test-Path -LiteralPath $localBuildConfig -PathType Leaf)) {
    Add-Failure "Local build configuration is missing; run the workspace setup before building."
}
$buildScript = Get-Content -LiteralPath (Join-Path $repo "scripts/build.ps1") -Raw
$privateToolLabel = "portable" + "-toolchain"
if ($buildScript.Contains($privateToolLabel, [StringComparison]::OrdinalIgnoreCase)) {
    Add-Failure "Build scripts must not expose the local tool directory name."
}

& git -C $repo cat-file -e "$base^{commit}" 2>$null
$baseAvailable = $LASTEXITCODE -eq 0
if (-not $baseAvailable -and $env:GITHUB_ACTIONS -eq "true") {
    & git -C $repo fetch --no-tags --depth=1 $manifest.upstream.repository $base
    if ($LASTEXITCODE -eq 0) {
        & git -C $repo cat-file -e "$base^{commit}" 2>$null
        $baseAvailable = $LASTEXITCODE -eq 0
    }
}
if (-not $baseAvailable) {
    Add-Failure "Pinned upstream commit is unavailable: $base"
}
else {
    $upstreamModules = @(
        "addons/counterstrikesharp/plugins/BotAI",
        "addons/counterstrikesharp/plugins/BotAimImprover",
        "addons/counterstrikesharp/plugins/BotBuy",
        "addons/counterstrikesharp/plugins/BotControllerImpl",
        "addons/counterstrikesharp/plugins/BotHiderImpl",
        "addons/counterstrikesharp/plugins/BotRandomizer",
        "addons/counterstrikesharp/plugins/BotState",
        "addons/counterstrikesharp/plugins/NadeSystem",
        "addons/counterstrikesharp/plugins/RoundDamageRecap",
        "overrides"
    )
    $changed = @(
        foreach ($module in $upstreamModules) {
            & git -C $repo diff --ignore-space-at-eol --quiet $base -- $module
            if ($LASTEXITCODE -eq 1) {
                & git -C $repo diff --ignore-space-at-eol --name-only $base -- $module
            }
            elseif ($LASTEXITCODE -ne 0) {
                Add-Failure "Unable to compare upstream enhanced-bot module: $module"
            }
        }
    )
    $allowedUpstreamChanges = @(
        "addons/counterstrikesharp/plugins/BotAimImprover/BotAimImprover.cs",
        "addons/counterstrikesharp/plugins/BotAimImprover/BotAimImprover.csproj",
        "addons/counterstrikesharp/plugins/BotBuy/BotBuy.cs",
        "addons/counterstrikesharp/plugins/BotBuy/BotBuy.csproj",
        "addons/counterstrikesharp/plugins/BotRandomizer/BotRandomizer.cs",
        "addons/counterstrikesharp/plugins/BotRandomizer/bot_randomizer_options.json",
        "addons/counterstrikesharp/plugins/BotControllerImpl/BotControllerImpl.csproj",
        "addons/counterstrikesharp/plugins/BotControllerImpl/BotController.NativeApi.cs",
        "addons/counterstrikesharp/plugins/BotControllerImpl/BotControllerApiImpl.cs",
        "addons/counterstrikesharp/plugins/BotControllerImpl/BotControllerCapability.cs",
        "addons/counterstrikesharp/plugins/BotControllerImpl/BotControllerImplPlugin.cs",
        "addons/counterstrikesharp/plugins/BotControllerImpl/MotionRecording.cs",
        "addons/counterstrikesharp/plugins/BotControllerImpl/ReplayDriver.cs",
        "addons/counterstrikesharp/plugins/BotHiderImpl/BotHiderImpl.csproj",
        "addons/counterstrikesharp/plugins/BotHiderImpl/BotHiderImplPlugin.cs",
        "addons/counterstrikesharp/plugins/NadeSystem/NadeSystem.cs",
        "addons/counterstrikesharp/plugins/NadeSystem/NadeSystem.csproj",
        "addons/counterstrikesharp/plugins/RoundDamageRecap/RoundDamageRecap.cs",
        "addons/counterstrikesharp/plugins/RoundDamageRecap/RoundDamageRecap.csproj",
        "overrides/Low/botprofile.db",
        "overrides/Medium/botprofile.db",
        "overrides/High/botprofile.db"
    )
    $unexpectedChanges = @($changed | Where-Object { $_ -notin $allowedUpstreamChanges })
    if ($unexpectedChanges.Count -gt 0) {
        Add-Failure "Upstream enhanced-bot modules were modified: $($unexpectedChanges -join ', ')"
    }
}

$botAiProject = Get-Content -LiteralPath (Join-Path $repo "addons/counterstrikesharp/plugins/BotAI/BotAI.csproj") -Raw
if ($botAiProject -match 'Tmp\\ArchiveV02\\Common') {
    Add-Failure "BotAI still references the machine-specific Common.dll path."
}

$botAimImprover = Get-Content -LiteralPath (Join-Path $repo "addons/counterstrikesharp/plugins/BotAimImprover/BotAimImprover.cs") -Raw
$botBehaviorPolicy = Get-Content -LiteralPath (Join-Path $repo "addons/counterstrikesharp/shared/MatchCore/BotBehaviorPolicy.cs") -Raw
if ($botAimImprover -match 'MemoryFunction|DynamicHook|\.Hook\(' -or
    $botAimImprover -match 'WindowsOffsets|LinuxOffsets|ReadIntPtr|ReadInt32|ReadByte' -or
    $botAimImprover -notmatch 'RegisterListener<Listeners\.OnTick>\(OnTick\)' -or
    $botAimImprover -notmatch 'pawn\?\.Bot' -or
    $botAimImprover -notmatch 'bot\.TargetSpot' -or
    $botAimImprover -notmatch 'BotAimPolicy\.SelectPriority' -or
    $botAimImprover -notmatch 'AimPointIndex' -or
    $botAimImprover -notmatch 'TargetSpot write verification failed' -or
    $botBehaviorPolicy -notmatch 'BotAimMode\.Head when string\.Equals\(weapon, "weapon_awp"' -or
    $botBehaviorPolicy -notmatch 'BotAimMode\.Head => BotAimPriority\.Head') {
    Add-Failure "BotAimImprover must use managed CCSBot schema targeting without native function hooks or manual offsets."
}
$panelBackend = Get-Content -LiteralPath (Join-Path $repo "Panel/src-tauri/src/lib.rs") -Raw
if ($panelBackend -match 'aim_supported: true' -or
    $panelBackend -notmatch 'aim_supported,' -or
    $panelBackend -notmatch '\.csbip/aim-runtime\.json' -or
    $panelBackend -notmatch 'aim_override_count') {
    Add-Failure "Panel backend must expose the managed bot aim modes on Windows."
}

$botBuy = Get-Content -LiteralPath (Join-Path $repo "addons/counterstrikesharp/plugins/BotBuy/BotBuy.cs") -Raw
if (([regex]::Matches($botBuy, 'AddTimer\(').Count -ne 1) -or
    ([regex]::Matches($botBuy, 'ScheduleRound\(').Count -lt 12) -or
    $botBuy -notmatch 'BotCallbackGeneration' -or
    $botBuy -notmatch 'TimerFlags\.STOP_ON_MAPCHANGE' -or
    $botBuy -notmatch 'ResolvePlayer\(userId\)' -or
    $botBuy -notmatch 'ManagedMatchRuntimeStore\.IsPurchasingAllowed' -or
    $botBuy -notmatch 'HasWeapon\(player, oldItem\)') {
    Add-Failure "BotBuy no longer guards delayed callbacks against invalid CounterStrikeSharp controllers."
}

$nadeSystem = Get-Content -LiteralPath (Join-Path $repo "addons/counterstrikesharp/plugins/NadeSystem/NadeSystem.cs") -Raw
if ($nadeSystem -notmatch 'if \(!bot\.IsValid\) return;' -or
    $nadeSystem -notmatch 'botPawn = bot\.PlayerPawn\?\.Value;' -or
    $nadeSystem -notmatch 'catch \(Exception\)\s*\{\s*return;\s*\}') {
    Add-Failure "NadeSystem no longer guards delayed callbacks against disconnected bot pawns."
}
$nadeDataRoot = Join-Path $repo "addons/counterstrikesharp/plugins/NadeSystem/grenades"
$nadeDataFiles = @(Get-ChildItem -LiteralPath $nadeDataRoot -Filter "*.json" -File -ErrorAction SilentlyContinue)
if ($nadeDataFiles.Count -ne 48) {
    Add-Failure "NadeSystem must ship the frozen 48-file grenade catalog; found $($nadeDataFiles.Count)."
}
foreach ($nadeDataFile in $nadeDataFiles) {
    try {
        $nadeEntries = @(Get-Content -LiteralPath $nadeDataFile.FullName -Raw | ConvertFrom-Json)
        if ($nadeEntries.Count -eq 0) {
            Add-Failure "NadeSystem grenade catalog is empty: $($nadeDataFile.Name)"
        }
    }
    catch {
        Add-Failure "NadeSystem grenade catalog is invalid: $($nadeDataFile.Name): $($_.Exception.Message)"
    }
}

$matchCoordinator = Get-Content -LiteralPath (Join-Path $repo "addons/counterstrikesharp/plugins/PlusMatchCoordinator/PlusMatchCoordinator.cs") -Raw
if ($matchCoordinator -notmatch 'private void EnsureInitialHumanSide\(\)' -or
    $matchCoordinator -notmatch 'if \(human\.Team != target\) human\.SwitchTeam\(target\);' -or
    ([regex]::Matches($matchCoordinator, 'EnsureInitialHumanSide\(\);').Count -ne 2) -or
    ([regex]::Matches($matchCoordinator, '\.SwitchTeam\(').Count -ne 1)) {
    Add-Failure "PlusMatchCoordinator may force the local player back to the initial side after halftime."
}

$roundDamageRecap = Get-Content -LiteralPath (Join-Path $repo "addons/counterstrikesharp/plugins/RoundDamageRecap/RoundDamageRecap.cs") -Raw
if ($roundDamageRecap -notmatch 'PlusManagedPaths\.ActiveMatchPath' -or
    $roundDamageRecap -notmatch 'PlusMatchCoordinator owns match statistics') {
    Add-Failure "RoundDamageRecap no longer yields PLUS match statistics ownership."
}

$matchPanel = Get-Content -LiteralPath (Join-Path $repo "Panel/src/panels/MatchPanel.tsx") -Raw
if ($matchPanel -notmatch 'localStorage\.getItem\("cs2bi\.matchDemoV2"\) === "1"' -or
    $matchPanel -notmatch 'match\.demoWarning') {
    Add-Failure "Match Panel no longer keeps GOTV opt-in with a performance warning."
}

$requiredSources = @(
    "Panel/src-tauri/src/lib.rs",
    "Panel/src/panels/KnifePresetModal.tsx",
    "Panel/src/panels/GlovePresetModal.tsx",
    "Panel/src/panels/WeaponPresetModal.tsx",
    "Panel/src/panels/MusicKitPresetModal.tsx",
    "Panel/src/panels/StickersPanel.tsx",
    "Panel/src/panels/StickersPanel.css",
    "Panel/src/data/stickerCatalog.json",
    "Panel/src/data/stickerCatalog.source.json",
    "Panel/src/data/stickerWeaponIds.json",
    "Panel/src/lib/stickerEditor.ts",
    "scripts/generate-sticker-catalog.mjs",
    "scripts/test-sticker-editor.mjs",
    "addons/counterstrikesharp/plugins/BotRandomizer/BotRandomizer.cs",
    "addons/counterstrikesharp/plugins/BotControllerImpl/BotControllerImplPlugin.cs",
    "addons/counterstrikesharp/plugins/BotRandomizer/bot_randomizer_options.json",
    "addons/counterstrikesharp/plugins/PlayerKnifeCustomizer/PlayerKnifeCustomizer.cs",
    "addons/counterstrikesharp/plugins/PlayerKnifeCustomizer/sticker_weapon_ids.json",
    "addons/counterstrikesharp/plugins/BotHiderImpl/BotHiderImplPlugin.cs"
)
foreach ($relative in $requiredSources) {
    Assert-File (Join-Path $repo $relative) $relative
}

$playerCosmetics = Get-Content -LiteralPath (Join-Path $repo "addons/counterstrikesharp/plugins/PlayerKnifeCustomizer/PlayerKnifeCustomizer.cs") -Raw
$giveHookStart = $playerCosmetics.IndexOf("private HookResult OnGiveNamedItemPost", [StringComparison]::Ordinal)
$nextMethodStart = $playerCosmetics.IndexOf("private static CCSPlayerController? GetPlayerFromItemServices", [StringComparison]::Ordinal)
if ($giveHookStart -lt 0 -or $nextMethodStart -le $giveHookStart) {
    Add-Failure "PlayerCosmetics purchased-weapon lifecycle guard is missing."
}
else {
    $giveHook = $playerCosmetics.Substring($giveHookStart, $nextMethodStart - $giveHookStart)
    if ($giveHook -notmatch "_applyTracker\.Begin\(playerHandle, CosmeticApplyPhase\.Guns\)" -or
        $giveHook -notmatch "ScheduleApplyCallbacks\(playerHandle, generation\)" -or
        $giveHook -match "ApplyPreset\(" -or
        $giveHook -match "_setAttrByName.*Invoke") {
        Add-Failure "PlayerCosmetics GiveNamedItem hook must defer native writes into the bounded generation pipeline."
    }
}
if ($playerCosmetics -notmatch "Server\.NextFrame\(\(\) => RunApplyPipeline\(playerHandle, generation, false\)\)" -or
    $playerCosmetics -notmatch "RetryDelays = \[0\.10f, 0\.25f, 0\.50f, 0\.90f\]" -or
    $playerCosmetics -notmatch "finalAttempt = index == ApplyPipelineContext\.RetryDelays\.Length - 1" -or
    $playerCosmetics -notmatch "player\.PawnIsAlive" -or
    $playerCosmetics -notmatch "TryBindContext\(playerHandle, generation, readyPawn\.Handle, \(int\)readyTeam\)") {
    Add-Failure "PlayerCosmetics generation pipeline no longer has bounded retries and Pawn/team context validation."
}
if ($playerCosmetics -notmatch "private static bool HasReadyAttributeLists\(CEconItemView item\)" -or
    ([regex]::Matches($playerCosmetics, "HasReadyAttributeLists\(item\)").Count -lt 3)) {
    Add-Failure "PlayerCosmetics native attribute handles are not validated at every write entry point."
}
if ($playerCosmetics -notmatch '"sticker slot \{sticker\.Slot\}"' -or
    $playerCosmetics -notmatch "BitConverter\.Int32BitsToSingle" -or
    $playerCosmetics -notmatch "TryMarkReequip\(player\.Handle, generation\)" -or
    ([regex]::Matches($playerCosmetics, 'ExecuteClientCommand\("lastinv"\)').Count -ne 2)) {
    Add-Failure "PlayerCosmetics sticker attributes or single-generation re-equip fallback are incomplete."
}
if ($playerCosmetics -match "RegisterListener<Listeners\.OnEntitySpawned>" -or
    $playerCosmetics -match "TryApplyDroppedKnife" -or
    $playerCosmetics -match "Server\.NextWorldUpdate") {
    Add-Failure "PlayerCosmetics must not retain raw entity pointers across world updates for dropped knives."
}

$jsonFiles = @(
    "addons/BotHider/bot_info.json",
    "addons/BotHider/gamedata.json",
    "addons/BotHider/map_whitelist.json",
    "Panel/src/data/gloveSkins.json",
    "Panel/src/data/musicKits.json",
    "Panel/src/data/skinImages.json",
    "Panel/src/data/skinNames.json",
    "Panel/src/data/weaponSkins.json",
    "Panel/src/data/stickerCatalog.json",
    "Panel/src/data/stickerWeaponIds.json",
    "addons/counterstrikesharp/plugins/PlayerKnifeCustomizer/sticker_ids.json",
    "addons/counterstrikesharp/plugins/PlayerKnifeCustomizer/sticker_weapon_ids.json",
    "addons/counterstrikesharp/plugins/PlayerKnifeCustomizer/player_gun_presets.json",
    "addons/counterstrikesharp/plugins/PlayerKnifeCustomizer/player_knife_presets.json",
    "addons/counterstrikesharp/plugins/PlayerKnifeCustomizer/skins_en.json",
    "addons/counterstrikesharp/plugins/PlayerKnifeCustomizer/weapon_skins.json"
)
$counts = @{}
foreach ($relative in $jsonFiles) {
    $counts[$relative] = Get-JsonCount $relative
}

$catalogA = Join-Path $repo "Panel/src/data/weaponSkins.json"
$catalogB = Join-Path $repo "addons/counterstrikesharp/plugins/PlayerKnifeCustomizer/weapon_skins.json"
if ((Get-FileHash -LiteralPath $catalogA -Algorithm SHA256).Hash -ne
    (Get-FileHash -LiteralPath $catalogB -Algorithm SHA256).Hash) {
    Add-Failure "Panel and plugin weapon catalogs are not identical."
}

$stickerSourcePath = Join-Path $repo "Panel/src/data/stickerCatalog.source.json"
try {
    $stickerSource = Get-Content -LiteralPath $stickerSourcePath -Raw | ConvertFrom-Json
    $stickerPanel = Join-Path $repo "Panel/src/data/stickerCatalog.json"
    $stickerPlugin = Join-Path $repo "addons/counterstrikesharp/plugins/PlayerKnifeCustomizer/sticker_ids.json"
    $stickerWeaponPanel = Join-Path $repo "Panel/src/data/stickerWeaponIds.json"
    $stickerWeaponPlugin = Join-Path $repo "addons/counterstrikesharp/plugins/PlayerKnifeCustomizer/sticker_weapon_ids.json"
    $stickerGenerator = Get-Content -LiteralPath (Join-Path $repo "scripts/generate-sticker-catalog.mjs") -Raw
    $panelHash = (Get-FileHash -LiteralPath $stickerPanel -Algorithm SHA256).Hash.ToLowerInvariant()
    $pluginHash = (Get-FileHash -LiteralPath $stickerPlugin -Algorithm SHA256).Hash.ToLowerInvariant()
    $weaponPanelHash = (Get-FileHash -LiteralPath $stickerWeaponPanel -Algorithm SHA256).Hash.ToLowerInvariant()
    $weaponPluginHash = (Get-FileHash -LiteralPath $stickerWeaponPlugin -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($stickerSource.schema_version -ne 1 -or
        $stickerSource.outputs.count -ne $counts["Panel/src/data/stickerCatalog.json"] -or
        $counts["Panel/src/data/stickerCatalog.json"] -ne $counts["addons/counterstrikesharp/plugins/PlayerKnifeCustomizer/sticker_ids.json"] -or
        $panelHash -ne [string]$stickerSource.outputs.panel_sha256 -or
        $pluginHash -ne [string]$stickerSource.outputs.plugin_ids_sha256 -or
        $counts["Panel/src/data/stickerWeaponIds.json"] -ne $stickerSource.capabilities.supported_weapon_count -or
        $counts["Panel/src/data/stickerWeaponIds.json"] -ne $counts["addons/counterstrikesharp/plugins/PlayerKnifeCustomizer/sticker_weapon_ids.json"] -or
        $weaponPanelHash -ne [string]$stickerSource.outputs.weapon_ids_sha256 -or
        $weaponPluginHash -ne [string]$stickerSource.outputs.weapon_ids_sha256 -or
        $stickerGenerator -notmatch [regex]::Escape([string]$stickerSource.commit) -or
        $stickerGenerator -notmatch [regex]::Escape([string]$stickerSource.capabilities.commit)) {
        Add-Failure "Sticker catalog source commits, counts, capabilities, or deterministic hashes do not match generated outputs."
    }
}
catch {
    Add-Failure "Sticker catalog metadata is invalid: $($_.Exception.Message)"
}

$matchCatalog = Get-Content -LiteralPath (Join-Path $repo "addons/counterstrikesharp/plugins/PlusMatchCoordinator/match_catalog.json") -Raw | ConvertFrom-Json
$featuredPlayers = @($matchCatalog.teams | ForEach-Object { $_.players } | Sort-Object -Unique)

function Assert-BotProfileContent([string]$Profile, [string]$Label) {
    foreach ($player in $featuredPlayers) {
        if ($Profile -notmatch ('"' + [regex]::Escape($player) + '"')) {
            Add-Failure "$Label is missing featured player $player."
        }
    }
    foreach ($match in [regex]::Matches($Profile, '(?m)^\s*LookAngleMaxAccel(?:Normal|Attacking)\s*=\s*(\S+)')) {
        $token = $match.Groups[1].Value
        $value = 0.0
        $valid = [double]::TryParse(
            $token,
            [Globalization.NumberStyles]::Float,
            [Globalization.CultureInfo]::InvariantCulture,
            [ref]$value
        )
        if (-not $valid -or [double]::IsNaN($value) -or [double]::IsInfinity($value) -or $value -lt 0 -or $value -gt 20000) {
            Add-Failure "$Label has invalid LookAngleMaxAccel value '$token'."
        }
    }
}

$botHiderImpl = Get-Content -LiteralPath (Join-Path $repo "addons/counterstrikesharp/plugins/BotHiderImpl/BotHiderImplPlugin.cs") -Raw
if ($botHiderImpl -notmatch "foreach \(int slot in managedSlots\)" -or
    $botHiderImpl -notmatch "player\.PlayerName = name" -or
    $botHiderImpl -notmatch 'ModuleVersion => "0\.3\.3"' -or
    $botHiderImpl -notmatch "EnsureBotInfoNameSource\(\)") {
    Add-Failure "BotHiderImpl no longer preserves the v0.3.3 managed-name integration."
}
$botHiderGameData = Get-Content -LiteralPath (Join-Path $repo "addons/BotHider/gamedata.json") -Raw
if ($botHiderGameData -notmatch '"CServerSideClient::SetName"' -or
    $botHiderGameData -notmatch '"CNetworkGameServer::PackEntities"') {
    Add-Failure "BotHider gamedata no longer contains the v0.3.3 name and identity targets."
}
foreach ($difficulty in @("Low", "Medium", "High")) {
    $profilePath = Join-Path $repo "overrides/$difficulty/botprofile.db"
    $profile = Get-Content -LiteralPath $profilePath -Raw
    Assert-BotProfileContent $profile "$difficulty botprofile.db"
}

$trackedGenerated = @(& git -C $repo ls-files | Where-Object {
    $_ -match '(^|/)(bin|obj|node_modules|dist|target|artifacts|\.cache)/'
})
if ($trackedGenerated.Count -gt 0) {
    Add-Failure "Generated paths are tracked: $($trackedGenerated -join ', ')"
}

if ($PackageRoot) {
    $package = [IO.Path]::GetFullPath($PackageRoot)
    $requiredPackageFiles = @(
        "addons/BotHider/bin/win64/BotHider.dll",
        "addons/BotController/bin/win64/BotController.dll",
        "addons/metamod/BotController.vdf",
        "addons/metamod/bin/win64/server.dll",
        "addons/counterstrikesharp/bin/win64/counterstrikesharp.dll",
        "addons/RayTrace/bin/win64/RayTrace.dll",
        "addons/counterstrikesharp/plugins/RayTraceImpl/RayTraceImpl.dll",
        "addons/counterstrikesharp/plugins/BotAI/BotAI.dll",
        "addons/counterstrikesharp/plugins/BotAimImprover/BotAimImprover.dll",
        "addons/counterstrikesharp/plugins/BotBuy/BotBuy.dll",
        "addons/counterstrikesharp/plugins/BotControllerImpl/BotControllerImpl.dll",
        "addons/counterstrikesharp/plugins/BotHiderImpl/BotHiderImpl.dll",
        "addons/counterstrikesharp/plugins/BotRandomizer/BotRandomizer.dll",
        "addons/counterstrikesharp/plugins/BotState/BotState.dll",
        "addons/counterstrikesharp/plugins/NadeSystem/NadeSystem.dll",
        "addons/counterstrikesharp/plugins/PlayerKnifeCustomizer/PlayerKnifeCustomizer.dll",
        "addons/counterstrikesharp/plugins/PlayerKnifeCustomizer/sticker_ids.json",
        "addons/counterstrikesharp/plugins/PlayerKnifeCustomizer/sticker_weapon_ids.json",
        "addons/counterstrikesharp/plugins/RoundDamageRecap/RoundDamageRecap.dll",
        "addons/counterstrikesharp/plugins/PlusMatchCoordinator/PlusMatchCoordinator.dll",
        "addons/counterstrikesharp/plugins/PlusMatchCoordinator/MatchCore.dll",
        "addons/counterstrikesharp/plugins/PlusMatchCoordinator/match_catalog.json",
        "addons/counterstrikesharp/plugins/PlusMatchCoordinator/open-rating-3.0-proxy-v1.json",
        "addons/counterstrikesharp/plugins/PlusMatchCoordinator/profiles/Low/botprofile.db",
        "addons/counterstrikesharp/plugins/PlusMatchCoordinator/profiles/Medium/botprofile.db",
        "addons/counterstrikesharp/plugins/PlusMatchCoordinator/profiles/High/botprofile.db",
        "addons/counterstrikesharp/shared/0Harmony/0Harmony.dll",
        "addons/counterstrikesharp/shared/BotHiderApi/BotHiderApi.dll",
        "addons/counterstrikesharp/shared/BotControllerApi/BotControllerApi.dll",
        "plus-payload-manifest.json",
        "LocalArena.exe",
        "README.md",
        "README.zh-CN.md",
        "LICENSE"
    )
    foreach ($relative in $requiredPackageFiles) {
        Assert-File (Join-Path $package $relative) "package file $relative"
    }
    $packagedNadeDataRoot = Join-Path $package "addons/counterstrikesharp/plugins/NadeSystem/grenades"
    $packagedNadeDataFiles = @(
        Get-ChildItem -LiteralPath $packagedNadeDataRoot -Filter "*.json" -File -ErrorAction SilentlyContinue
    )
    $sourceNadeNames = @($nadeDataFiles.Name | Sort-Object)
    $packagedNadeNames = @($packagedNadeDataFiles.Name | Sort-Object)
    $nadeNameDifference = @(Compare-Object $sourceNadeNames $packagedNadeNames)
    if ($nadeNameDifference.Count -gt 0) {
        Add-Failure "Package grenade catalog does not match the frozen source file set."
    }
    foreach ($nadeDataFile in $packagedNadeDataFiles) {
        try {
            if (@(Get-Content -LiteralPath $nadeDataFile.FullName -Raw | ConvertFrom-Json).Count -eq 0) {
                Add-Failure "Packaged grenade catalog is empty: $($nadeDataFile.Name)"
            }
        }
        catch {
            Add-Failure "Packaged grenade catalog is invalid: $($nadeDataFile.Name): $($_.Exception.Message)"
        }
    }
    $previewNotice = Join-Path $package "PREVIEW-NOTICE.txt"
    if ($ExpectedPackageVersion -match '-Preview\.\d+$') {
        Assert-File $previewNotice "package preview notice"
    }
    elseif (Test-Path -LiteralPath $previewNotice) {
        Add-Failure "Official package must not contain PREVIEW-NOTICE.txt."
    }
    $packagedPanel = Join-Path $package "LocalArena.exe"
    $builtPanel = Join-Path $repo "Panel/src-tauri/target/release/cs2-bot-improver-plus-panel.exe"
    if ((Test-Path -LiteralPath $packagedPanel) -and (Test-Path -LiteralPath $builtPanel) -and
        ((Get-FileHash -LiteralPath $packagedPanel -Algorithm SHA256).Hash -ne
            (Get-FileHash -LiteralPath $builtPanel -Algorithm SHA256).Hash)) {
        Add-Failure "Packaged Panel is not the current production Release build."
    }
    $nestedShared = Join-Path $package "addons/counterstrikesharp/plugins/BotHiderImpl/shared"
    if (Test-Path -LiteralPath $nestedShared) {
        Add-Failure "Package contains an invalid nested BotHiderImpl/shared directory."
    }
    $linuxBotHiderVdf = Join-Path $package "addons/metamod/BotHider.linux.vdf"
    if (Test-Path -LiteralPath $linuxBotHiderVdf) {
        Add-Failure "Windows package contains the Linux BotHider loader."
    }
    $packagedGameInfo = @(Get-ChildItem -LiteralPath $package -Recurse -Filter "gameinfo.gi" -File)
    if ($packagedGameInfo.Count -gt 0) {
        Add-Failure "Package must not contain stale gameinfo.gi files: $($packagedGameInfo.FullName -join ', ')"
    }

    $payloadManifestPath = Join-Path $package "plus-payload-manifest.json"
    if (Test-Path -LiteralPath $payloadManifestPath) {
        try {
            $payloadManifest = Get-Content -LiteralPath $payloadManifestPath -Raw | ConvertFrom-Json
            if ($payloadManifest.schema_version -ne 1 -or $payloadManifest.package_version -ne $ExpectedPackageVersion) {
                Add-Failure "Package payload manifest has an unexpected schema or version."
            }
            $manifestPaths = @{}
            $manifestPolicies = @{}
            $manifestOwnership = @{}
            foreach ($entry in $payloadManifest.entries) {
                $relative = [string]$entry.path
                if ($manifestPaths.ContainsKey($relative)) {
                    Add-Failure "Package payload manifest contains a duplicate path: $relative"
                    continue
                }
                $manifestPaths[$relative] = $true
                $manifestPolicies[$relative] = [string]$entry.restore_policy
                $manifestOwnership[$relative] = [string]$entry.ownership
                if ($relative -notmatch '^(addons|cfg|overrides)/' -or $relative -match '(^|/)\.\.(/|$)') {
                    Add-Failure "Package payload manifest contains an unsafe path: $relative"
                    continue
                }
                $file = Join-Path $package $relative
                if (-not (Test-Path -LiteralPath $file -PathType Leaf)) {
                    Add-Failure "Package payload manifest references a missing file: $relative"
                    continue
                }
                $actualSize = (Get-Item -LiteralPath $file).Length
                $actualHash = (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash.ToLowerInvariant()
                if ($actualSize -ne [long]$entry.size -or $actualHash -ne ([string]$entry.sha256).ToLowerInvariant()) {
                    Add-Failure "Package payload manifest verification failed: $relative"
                }
            }
            $payloadFiles = foreach ($topLevel in @("addons", "cfg", "overrides")) {
                $root = Join-Path $package $topLevel
                if (Test-Path -LiteralPath $root) {
                    Get-ChildItem -LiteralPath $root -File -Recurse | ForEach-Object {
                        [IO.Path]::GetRelativePath($package, $_.FullName).Replace("\", "/")
                    }
                }
            }
            foreach ($relative in $payloadFiles) {
                if (-not $manifestPaths.ContainsKey($relative)) {
                    Add-Failure "Package payload file is not tracked by the manifest: $relative"
                }
            }
            $expectedPreserveConfigs = @(
                "addons/counterstrikesharp/plugins/PlayerKnifeCustomizer/player_knife_presets.json",
                "addons/counterstrikesharp/plugins/PlayerKnifeCustomizer/player_gun_presets.json",
                "addons/counterstrikesharp/plugins/BotRandomizer/bot_randomizer_options.json",
                "cfg/my_bot_ffa_config.cfg",
                "cfg/my_bot_normal_config.cfg",
                "overrides/botprofile.vpk"
            )
            foreach ($relative in $expectedPreserveConfigs) {
                if ($manifestPolicies[$relative] -ne "preserve-config") {
                    Add-Failure "Mutable player configuration is not protected by preserve-config: $relative"
                }
            }
            foreach ($relative in $manifestPaths.Keys | Where-Object {
                $_ -like "addons/counterstrikesharp/plugins/PlusMatchCoordinator/*"
            }) {
                if ($manifestOwnership[$relative] -ne "plus") {
                    Add-Failure "PlusMatchCoordinator payload is not Plus-owned: $relative"
                }
            }
        }
        catch {
            Add-Failure "Package payload manifest is invalid: $($_.Exception.Message)"
        }
    }

    $botProfileVpks = @(
        "overrides/botprofile.vpk",
        "overrides/Low/botprofile.vpk",
        "overrides/Medium/botprofile.vpk",
        "overrides/High/botprofile.vpk"
    )
    foreach ($relative in $botProfileVpks) {
        $vpk = Join-Path $package $relative
        Assert-File $vpk "package file $relative"
        if (Test-Path -LiteralPath $vpk) {
            try {
                $entries = @(Get-VpkEntryPaths $vpk)
                if ($entries.Count -ne 1 -or $entries[0] -ne "botprofile.db") {
                    Add-Failure "Package $relative must contain only botprofile.db; found: $($entries -join ', ')"
                }
                else {
                    $embedded = Read-VpkEmbeddedEntry $vpk "botprofile.db"
                    $profile = [Text.Encoding]::UTF8.GetString($embedded.Bytes)
                    Assert-BotProfileContent $profile "Package $relative"
                }
            }
            catch {
                Add-Failure "Invalid package VPK $relative`: $($_.Exception.Message)"
            }
        }
    }

    $native = Join-Path $package "addons/BotHider/bin/win64/BotHider.dll"
    if (Test-Path -LiteralPath $native) {
        $nativeHash = (Get-FileHash -LiteralPath $native -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($nativeHash -ne $manifest.botHider.windowsDllSha256.ToLowerInvariant()) {
            Add-Failure "Package BotHider.dll is not the verified v0.3.3 build: $nativeHash"
        }
    }

    $pinnedRuntimeFiles = @(
        @{ Relative = "addons/metamod/bin/win64/server.dll"; Hash = $manifest.metamod.windowsLoaderSha256; Label = "Metamod" },
        @{ Relative = "addons/counterstrikesharp/bin/win64/counterstrikesharp.dll"; Hash = $manifest.counterStrikeSharp.windowsCoreSha256; Label = "CounterStrikeSharp" },
        @{ Relative = "addons/RayTrace/bin/win64/RayTrace.dll"; Hash = $manifest.rayTrace.windowsDllSha256; Label = "RayTrace native" },
        @{ Relative = "addons/counterstrikesharp/plugins/RayTraceImpl/RayTraceImpl.dll"; Hash = $manifest.rayTrace.cssImplSha256; Label = "RayTrace CSS" }
    )
    foreach ($runtime in $pinnedRuntimeFiles) {
        $runtimePath = Join-Path $package $runtime.Relative
        if (Test-Path -LiteralPath $runtimePath) {
            $runtimeHash = (Get-FileHash -LiteralPath $runtimePath -Algorithm SHA256).Hash.ToLowerInvariant()
            if ($runtimeHash -ne $runtime.Hash.ToLowerInvariant()) {
                Add-Failure "$($runtime.Label) does not match the pinned engine-26 runtime: $runtimeHash"
            }
        }
    }

    $builtPlugins = @(
        @{ Name = "BotAI"; Framework = "net10.0" },
        @{ Name = "BotAimImprover"; Framework = "net10.0" },
        @{ Name = "BotBuy"; Framework = "net8.0" },
        @{ Name = "BotControllerImpl"; Framework = "net10.0" },
        @{ Name = "BotRandomizer"; Framework = "net10.0" },
        @{ Name = "NadeSystem"; Framework = "net10.0" },
        @{ Name = "RoundDamageRecap"; Framework = "net10.0" }
        @{ Name = "PlusMatchCoordinator"; Framework = "net8.0" }
    )
    foreach ($plugin in $builtPlugins) {
        $packageDll = Join-Path $package "addons/counterstrikesharp/plugins/$($plugin.Name)/$($plugin.Name).dll"
        $buildDll = Join-Path $repo "addons/counterstrikesharp/plugins/$($plugin.Name)/bin/Release/$($plugin.Framework)/$($plugin.Name).dll"
        if ((Test-Path -LiteralPath $packageDll) -and (Test-Path -LiteralPath $buildDll) -and
            ((Get-FileHash -LiteralPath $packageDll -Algorithm SHA256).Hash -ne
                (Get-FileHash -LiteralPath $buildDll -Algorithm SHA256).Hash)) {
            Add-Failure "Package $($plugin.Name) is not the current source build."
        }
    }
}

if ($failures.Count -gt 0) {
    $failures | ForEach-Object { Write-Error $_ -ErrorAction Continue }
    exit 1
}

Write-Host "Workspace verification passed."
Write-Host "Bot identities: $($counts['addons/BotHider/bot_info.json'])"
Write-Host "Weapon skins: $($counts['Panel/src/data/weaponSkins.json'])"
Write-Host "Glove skins: $($counts['Panel/src/data/gloveSkins.json'])"
Write-Host "Music kits: $($counts['Panel/src/data/musicKits.json'])"
if ($PackageRoot) { Write-Host "Package layout verified: $([IO.Path]::GetFullPath($PackageRoot))" }
