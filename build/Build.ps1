[CmdletBinding()]
param(
    [ValidateSet('Windows', 'Linux', 'All')]
    [string]$Platform = 'All',
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [string]$OutputRoot = (Join-Path $PSScriptRoot '..' 'artifacts'),
    [switch]$DownloadDependencies,
    [switch]$IncludeRuntime,
    [switch]$SkipCompile,
    [switch]$RulesUnchanged
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$OutputRoot = if ([IO.Path]::IsPathRooted($OutputRoot)) {
    [IO.Path]::GetFullPath($OutputRoot)
} else {
    [IO.Path]::GetFullPath((Join-Path $RepoRoot $OutputRoot))
}
$DependencyRoot = Join-Path $RepoRoot '.build-dependencies'

function Copy-Tree([string]$Source, [string]$Destination) {
    if (-not (Test-Path $Source)) { throw "Required directory not found: $Source" }
    New-Item -ItemType Directory -Force -Path $Destination | Out-Null
    Copy-Item (Join-Path $Source '*') $Destination -Recurse -Force
}

function Find-ComponentDirectory([string]$Root, [string]$RelativePath) {
    $suffix = '/' + $RelativePath.Replace('\', '/').Trim('/')
    Get-ChildItem $Root -Recurse -Directory |
        Where-Object {
            $normalized = $_.FullName.Replace('\', '/')
            $normalized.EndsWith($suffix, [StringComparison]::OrdinalIgnoreCase)
        } |
        Select-Object -First 1
}

function Get-GitHubRelease([string]$Repository) {
    $uri = "https://api.github.com/repos/$Repository/releases/latest"
    $headers = @{ 'User-Agent' = 'CS2-Bot-Improver-build' }
    $token = if ($env:GITHUB_TOKEN) { $env:GITHUB_TOKEN } elseif ($env:GH_TOKEN) { $env:GH_TOKEN } else { $null }
    if ($token) { $headers.Authorization = "Bearer $token" }
    try {
        return Invoke-RestMethod -Uri $uri -Headers $headers
    } catch {
        throw "Unable to query latest release for ${Repository}: $($_.Exception.Message)"
    }
}

function Get-RepositoryReference([string]$Repository, [string]$FileName) {
    $repoName = ($Repository -split '/')[1]
    $release = Get-GitHubRelease $Repository
    $tag = [string]$release.tag_name
    if ([string]::IsNullOrWhiteSpace($tag)) { throw "Latest release of $Repository has no tag." }
    $cache = Join-Path $DependencyRoot "references/$repoName/$tag/$FileName"
    if (Test-Path $cache) { return (Resolve-Path $cache).Path }
    New-Item -ItemType Directory -Force -Path (Split-Path $cache) | Out-Null
    $asset = @($release.assets |
        Where-Object {
            $_.name -match '(?i)\.(zip|tar\.gz|tgz)$' -and
            $_.name -notmatch '(?i)([-_.](source|symbols|debug|example)([-_.]|$))' 
        } |
        Select-Object -First 1)
    if (-not $asset) { throw "No downloadable archive found in latest release of $Repository." }
    $archive = Join-Path $DependencyRoot "references/$repoName/$tag/$($asset.name)"
    if (-not (Test-Path $archive)) {
        Write-Host "Downloading $($asset.name) from $Repository..."
        Invoke-WebRequest -Uri $asset.browser_download_url -OutFile $archive
    }
    $extract = Join-Path $DependencyRoot "references/$($Repository.Split('/')[1])/$tag/extracted"
    if (-not (Test-Path $extract)) {
        New-Item -ItemType Directory -Force -Path $extract | Out-Null
        if ($archive -match '\.zip$') { Expand-Archive $archive $extract -Force }
        else { tar -xzf $archive -C $extract }
    }
    $found = Get-ChildItem $extract -Recurse -File -Filter $FileName | Select-Object -First 1
    if (-not $found) { throw "Release $Repository did not contain $FileName." }
    Copy-Item $found.FullName $cache -Force
    return (Resolve-Path $cache).Path
}

function Get-GitHubAsset([string]$Repository, [string]$Pattern, [string]$CacheName) {
    $repoName = ($Repository -split '/')[1]
    $release = Get-GitHubRelease $Repository
    $tag = [string]$release.tag_name
    if ([string]::IsNullOrWhiteSpace($tag)) { throw "Latest release of $Repository has no tag." }
    $asset = @($release.assets |
        Where-Object { $_.name -match $Pattern -and $_.name -notmatch '(?i)([-_.](source|symbols|debug|example)([-_.]|$))' -and $_.name -match '(?i)\.(zip|tar\.gz|tgz)$' } |
        Select-Object -First 1)
    if (-not $asset) { throw "No release asset in $Repository matched '$Pattern'." }
    $archiveDirectory = Join-Path $DependencyRoot "archives/$repoName/$tag"
    $archive = Join-Path $archiveDirectory $asset.name
    if (Test-Path $archive) { return (Resolve-Path $archive).Path }
    New-Item -ItemType Directory -Force -Path $archiveDirectory | Out-Null
    Write-Host "Downloading $($asset.name) from $Repository..."
    Invoke-WebRequest -Uri $asset.browser_download_url -OutFile $archive
    return (Resolve-Path $archive).Path
}

function Add-ComponentRuntime([string]$Repository, [string]$Pattern, [string]$CacheName, [string]$Platform, [string]$Destination, [hashtable]$Components) {
    if (-not $DownloadDependencies) { throw "-IncludeRuntime requires -DownloadDependencies." }
    $archive = Get-GitHubAsset $Repository $Pattern "$Platform-$CacheName.archive"
    $extract = Join-Path $DependencyRoot "extracted/$Platform/$CacheName"
    if (Test-Path $extract) { Remove-Item $extract -Recurse -Force }
    Expand-DependencyArchive $archive $extract
    foreach ($component in $Components.Keys) {
        $source = Find-ComponentDirectory $extract $component
        if (-not $source) { throw "$Repository release did not contain $component." }
        Copy-Tree $source.FullName (Join-Path $Destination $Components[$component])
    }
}

function Expand-DependencyArchive([string]$Archive, [string]$Destination) {
    New-Item -ItemType Directory -Force -Path $Destination | Out-Null
    if ($Archive -match '\.zip$') { Expand-Archive $Archive $Destination -Force }
    elseif ($Archive -match '\.(tar\.gz|tgz)$') { tar -xzf $Archive -C $Destination }
    else { throw "Unsupported runtime archive format: $Archive" }
}

function Add-OfficialRuntime([string]$Platform, [string]$Destination) {
    if (-not $DownloadDependencies) { throw "-IncludeRuntime requires -DownloadDependencies." }
    $windowsPlatform = $Platform -eq 'Windows'
    $mmPattern = if ($windowsPlatform) { '(?i)^mmsource-.*-windows\.zip$' } else { '(?i)^mmsource-.*-linux\.tar\.gz$' }
    $cssPattern = if ($windowsPlatform) { '(?i)with-runtime.*(windows|win).*\.(zip|tar\.gz)$' } else { '(?i)with-runtime.*(linux|linuxsteamrt).*\.(zip|tar\.gz)$' }
    $mm = Get-GitHubAsset 'alliedmodders/metamod-source' $mmPattern "metamod-$($Platform.ToLowerInvariant()).archive"
    $css = Get-GitHubAsset 'roflmuffin/CounterStrikeSharp' $cssPattern "counterstrikesharp-$($Platform.ToLowerInvariant()).archive"
    $extractRoot = Join-Path $DependencyRoot "extracted/$Platform"
    if (Test-Path $extractRoot) { Remove-Item $extractRoot -Recurse -Force }
    $mmRoot = Join-Path $extractRoot 'metamod'
    $cssRoot = Join-Path $extractRoot 'counterstrikesharp'
    Expand-DependencyArchive $mm $mmRoot
    Expand-DependencyArchive $css $cssRoot
    # The Metamod archive contains the CS2 loader VDFs beside addons/metamod.
    # Keep the package layout identical to the game directory by copying them
    # into the package's addons root, while still extracting only this archive.
    $metamod = Get-ChildItem $mmRoot -Recurse -Directory -Filter metamod | Select-Object -First 1
    $counterStrikeSharp = Get-ChildItem $cssRoot -Recurse -Directory -Filter counterstrikesharp | Select-Object -First 1
    if (-not $metamod) { throw "Metamod archive did not contain an addons/metamod directory." }
    if (-not $counterStrikeSharp) { throw "CounterStrikeSharp archive did not contain an addons/counterstrikesharp directory." }
    Copy-Tree $metamod.FullName (Join-Path $Destination 'addons/metamod')
    $metamodRoot = Split-Path $metamod.FullName -Parent
    Copy-Item (Join-Path $metamodRoot '*.vdf') (Join-Path $Destination 'addons') -Force -ErrorAction SilentlyContinue
    Copy-Tree $counterStrikeSharp.FullName (Join-Path $Destination 'addons/counterstrikesharp')
    $cssMetamod = Get-ChildItem $cssRoot -Recurse -File -Filter counterstrikesharp.vdf | Select-Object -First 1
    if ($cssMetamod) {
        Copy-Item $cssMetamod.FullName (Join-Path $Destination 'addons/metamod/counterstrikesharp.vdf') -Force
    }
    $componentPattern = if ($windowsPlatform) { '(?i)(windows|win).*\.(zip|tar\.gz)$' } else { '(?i)(linux|linuxsteamrt).*\.(zip|tar\.gz)$' }
    Add-ComponentRuntime 'FUNPLAY-pro-CS2/Ray-Trace' $componentPattern 'Ray-Trace' $Platform $Destination @{
        'RayTrace' = 'addons/RayTrace'
        'metamod' = 'addons/metamod'
    }
    Add-ComponentRuntime 'FUNPLAY-pro-CS2/Ray-Trace' '(?i)CSS-API.*\.(zip|tar\.gz|tgz)$' 'Ray-Trace-CSS-API' $Platform $Destination @{
        'counterstrikesharp/plugins/RayTraceImpl' = 'addons/counterstrikesharp/plugins/RayTraceImpl'
        'counterstrikesharp/shared/RayTraceApi' = 'addons/counterstrikesharp/shared/RayTraceApi'
    }
    Add-ComponentRuntime 'XBribo/CS2-Bot-Controller' $componentPattern 'CS2-Bot-Controller' $Platform $Destination @{
        'addons/BotController' = 'addons/BotController'
    }
    Add-ComponentRuntime 'XBribo/CS2-Bot-Controller' '(?i)CSS-API.*\.zip$' 'CS2-Bot-Controller-CSS-API' $Platform $Destination @{
        'addons/counterstrikesharp/shared/BotControllerApi' = 'addons/counterstrikesharp/shared/BotControllerApi'
    }
    Add-ComponentRuntime 'XBribo/CS2-Bot-Hider' $componentPattern 'CS2-Bot-Hider' $Platform $Destination @{
        'addons/BotHider' = 'addons/BotHider'
        'addons/counterstrikesharp/shared/BotHiderApi' = 'addons/counterstrikesharp/shared/BotHiderApi'
        'addons/counterstrikesharp/shared/0Harmony' = 'addons/counterstrikesharp/shared/0Harmony'
    }
    Add-ComponentRuntime 'XBribo/CS2-Bot-Vision' $componentPattern 'CS2-Bot-Vision' $Platform $Destination @{
        'addons/BotVision' = 'addons/BotVision'
    }
}

function Add-LocalTemplates([string]$Platform, [string]$Destination) {
    # These files are game configuration templates, not platform binaries.
    # The Linux and Windows release layouts both carry the same files.
    $templateRoot = Join-Path $PSScriptRoot 'templates/windows'
    Copy-Item (Join-Path $templateRoot 'gameinfo.gi') $Destination -Force
    $backup = Join-Path $Destination 'backup'
    New-Item -ItemType Directory -Force -Path $backup | Out-Null
    New-Item -ItemType Directory -Force -Path (Join-Path $backup 'Online'), (Join-Path $backup 'WithBots') | Out-Null
    Copy-Item (Join-Path $templateRoot 'backup/Online.gameinfo.gi') (Join-Path $backup 'Online/gameinfo.gi') -Force
    Copy-Item (Join-Path $templateRoot 'backup/WithBots.gameinfo.gi') (Join-Path $backup 'WithBots/gameinfo.gi') -Force
    $configRoot = Join-Path $Destination 'addons/counterstrikesharp/configs'
    New-Item -ItemType Directory -Force -Path $configRoot | Out-Null
    Copy-Item (Join-Path $PSScriptRoot 'templates/core.json') (Join-Path $configRoot 'core.json') -Force
    $botHiderRoot = Join-Path $Destination 'addons/BotHider'
    New-Item -ItemType Directory -Force -Path $botHiderRoot | Out-Null
    Copy-Item (Join-Path $PSScriptRoot 'templates/map_whitelist.json') (Join-Path $botHiderRoot 'map_whitelist.json') -Force
}

function New-ProjectReferenceShim([string]$ProjectName) {
    $map = @{
        'BotControllerImpl' = @{ Relative = 'BotControllerApi'; Source = Join-Path $RepoRoot 'addons/counterstrikesharp/shared/BotControllerApi' }
        'BotHiderImpl' = @{ Relative = 'BotHiderApi'; Source = Join-Path $RepoRoot 'addons/counterstrikesharp/shared/BotHiderApi' }
    }
    if (-not $map.ContainsKey($ProjectName)) { return $null }
    $shim = Join-Path $RepoRoot "addons/counterstrikesharp/plugins/$($map[$ProjectName].Relative)"
    if (Test-Path $shim) { return $null }
    New-Item -ItemType Directory -Force -Path $shim | Out-Null
    Get-ChildItem $map[$ProjectName].Source -File | Where-Object { $_.Extension -in @('.cs', '.csproj') } | ForEach-Object {
        Copy-Item $_.FullName (Join-Path $shim $_.Name) -Force
    }
    return $shim
}

function Build-Plugins([string]$Destination) {
    $runtimeLibraries = @{
        'RayTraceApi.dll' = @{ Repository = 'FUNPLAY-pro-CS2/Ray-Trace' }
        'BotControllerApi.dll' = @{ Repository = 'XBribo/CS2-Bot-Controller' }
    }
    $sharedProjects = Get-ChildItem (Join-Path $RepoRoot 'addons/counterstrikesharp/shared') -Filter *.csproj -File
    foreach ($project in $sharedProjects) {
        Write-Host "Building shared API $($project.BaseName)..."
        dotnet build $project.FullName -c $Configuration --nologo
        if ($LASTEXITCODE -ne 0) { throw "dotnet build failed for $($project.FullName)" }
        $builtDll = Get-ChildItem (Join-Path $project.DirectoryName "bin/$Configuration") -Recurse -Filter "$($project.BaseName).dll" -File | Select-Object -First 1
        if (-not $builtDll) { throw "Build output for $($project.BaseName) was not found." }
        $target = Join-Path $Destination "addons/counterstrikesharp/shared/$($project.BaseName)"
        New-Item -ItemType Directory -Force -Path $target | Out-Null
        Copy-Item $builtDll.FullName (Join-Path $target $builtDll.Name) -Force
    }

    $projects = Get-ChildItem (Join-Path $RepoRoot 'addons/counterstrikesharp/plugins') -Recurse -Filter *.csproj |
        Where-Object {
            $normalizedPath = $_.FullName.Replace('\', '/')
            $normalizedPath -notmatch '/disabled/' -and
            $normalizedPath -notmatch '/libs/' -and
            $_.BaseName -ne 'Common'
        }
    foreach ($project in $projects) {
        $referenceNames = if ($project.BaseName -in @('BotAimImprover', 'NadeSystem')) { @('RayTraceApi.dll') } elseif ($project.BaseName -eq 'BotState') { @('BotControllerApi.dll') } else { @() }
        foreach ($referenceName in $referenceNames) {
            $reference = $runtimeLibraries[$referenceName]
            if (-not $DownloadDependencies) { throw "Building plugins requires -DownloadDependencies to resolve '$referenceName' from the latest $($reference.Repository) release." }
            $source = Get-RepositoryReference $reference.Repository $referenceName
            $libDir = Join-Path $project.DirectoryName 'libs'
            New-Item -ItemType Directory -Force -Path $libDir | Out-Null
            Copy-Item $source (Join-Path $libDir $referenceName) -Force
        }
        Write-Host "Building $($project.BaseName)..."
        $shim = New-ProjectReferenceShim $project.BaseName
        try {
            dotnet build $project.FullName -c $Configuration --nologo
            if ($LASTEXITCODE -ne 0) { throw "dotnet build failed for $($project.FullName)" }
        } finally {
            if ($shim -and (Test-Path $shim)) { Remove-Item $shim -Recurse -Force }
        }
        $out = Join-Path $project.DirectoryName "bin/$Configuration"
        $target = Join-Path $Destination "addons/counterstrikesharp/plugins/$($project.BaseName)"
        $builtDll = Get-ChildItem $out -Recurse -Filter "$($project.BaseName).dll" -File | Select-Object -First 1
        if (-not $builtDll) { throw "Build output for $($project.BaseName) was not found under $out." }
        New-Item -ItemType Directory -Force -Path $target | Out-Null
        Copy-Item $builtDll.FullName (Join-Path $target $builtDll.Name) -Force
        foreach ($sidecar in @("$($project.BaseName).deps.json", "$($project.BaseName).runtimeconfig.json", "$($project.BaseName).pdb")) {
            $file = Join-Path $builtDll.DirectoryName $sidecar
            if (Test-Path $file) { Copy-Item $file (Join-Path $target $sidecar) -Force }
        }
        $sourceFiles = Get-ChildItem $project.DirectoryName -Recurse -File |
            Where-Object {
                $normalizedPath = $_.FullName.Replace('\', '/')
                $normalizedPath -notmatch '/(bin|obj|libs|disabled)/' -and
                $_.Extension -notin @('.cs', '.csproj', '.user')
            }
        foreach ($sourceFile in $sourceFiles) {
            $relative = $sourceFile.FullName.Substring($project.DirectoryName.Length).TrimStart([char[]]('/\\'))
            $destinationFile = Join-Path $target $relative
            New-Item -ItemType Directory -Force -Path (Split-Path $destinationFile) | Out-Null
            Copy-Item $sourceFile.FullName $destinationFile -Force
        }
    }
}

function New-Package([string]$Name, [string]$Runtime, [bool]$KeepRules) {
    $stage = Join-Path $OutputRoot "stage-$Name"
    if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
    New-Item -ItemType Directory -Force -Path $stage | Out-Null
    if ($IncludeRuntime) { Add-OfficialRuntime $Runtime $stage }
    New-Item -ItemType Directory -Force -Path (Join-Path $stage 'addons/metamod') | Out-Null
    Copy-Item (Join-Path $RepoRoot 'addons/metamod/*.vdf') (Join-Path $stage 'addons/metamod') -Force -ErrorAction SilentlyContinue
    foreach ($addon in @('BotController', 'BotHider', 'BotVision')) {
        $source = Join-Path $RepoRoot "addons/$addon"
        if (Test-Path $source) { Copy-Tree $source (Join-Path $stage "addons/$addon") }
    }
    Copy-Tree (Join-Path $RepoRoot 'cfg') (Join-Path $stage 'cfg')
    Copy-Tree (Join-Path $RepoRoot 'overrides') (Join-Path $stage 'overrides')
    Copy-Item (Join-Path $RepoRoot 'Commands.txt') $stage -Force
    Add-LocalTemplates $Runtime $stage
    if (-not $KeepRules) {
        $normal = Join-Path $stage 'overrides/Medium/botprofile.db'
        if (Test-Path $normal) { Copy-Item $normal (Join-Path $stage 'overrides/botprofile.db') -Force }
    }
    if (-not $SkipCompile) { Build-Plugins $stage }
    if (-not $IncludeRuntime) {
        $unexpectedRuntime = @(
            (Join-Path $stage 'addons/metamod/bin'),
            (Join-Path $stage 'addons/counterstrikesharp/bin'),
            (Join-Path $stage 'addons/counterstrikesharp/api'),
            (Join-Path $stage 'addons/counterstrikesharp/dotnet')
        ) | Where-Object { Test-Path $_ }
        if ($unexpectedRuntime) { throw "Plugin-only package unexpectedly contains runtime files: $($unexpectedRuntime -join ', ')" }
    }
    New-Item -ItemType Directory -Force -Path $OutputRoot | Out-Null
    $zip = Join-Path $OutputRoot "$Name.zip"
    if (Test-Path $zip) { Remove-Item $zip -Force }
    Compress-Archive (Join-Path $stage '*') $zip -CompressionLevel Optimal
    Write-Host "Created $zip"
}

New-Item -ItemType Directory -Force -Path $OutputRoot | Out-Null
if ($Platform -in @('Windows', 'All')) {
    New-Package 'CS2BotImprover' 'Windows' ([bool]$RulesUnchanged)
    if (-not $RulesUnchanged) { New-Package 'CS2BotImprover_rules_unchanged' 'Windows' $true }
}
if ($Platform -in @('Linux', 'All')) {
    New-Package 'CS2BotImprover_for_Linux' 'Linux' $true
}
