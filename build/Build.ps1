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
$ReleaseCache = @{}

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

function Copy-ComponentFile([string]$Root, [string]$RelativePath, [string]$Destination) {
    $suffix = '/' + $RelativePath.Replace('\', '/').Trim('/')
    $source = Get-ChildItem $Root -Recurse -File |
        Where-Object {
            $_.FullName.Replace('\', '/').EndsWith($suffix, [StringComparison]::OrdinalIgnoreCase)
        } |
        Select-Object -First 1
    if (-not $source) { throw "Component archive did not contain $RelativePath." }
    New-Item -ItemType Directory -Force -Path (Split-Path $Destination) | Out-Null
    Copy-Item $source.FullName $Destination -Force
}

function Get-GitHubRelease([string]$Repository, [switch]$Prerelease) {
    $cacheKey = "$($Repository.ToLowerInvariant()):$Prerelease"
    if ($ReleaseCache.ContainsKey($cacheKey)) { return $ReleaseCache[$cacheKey] }
    $uri = "https://api.github.com/repos/$Repository/releases/latest"
    $headers = @{ 'User-Agent' = 'CS2-Bot-Improver-build' }
    $token = if ($env:GITHUB_TOKEN) { $env:GITHUB_TOKEN } elseif ($env:GH_TOKEN) { $env:GH_TOKEN } else { $null }
    if ($token) { $headers.Authorization = "Bearer $token" }
    try {
        if ($Prerelease) {
            $page = 1
            do {
                $releases = Invoke-RestMethod -Uri "https://api.github.com/repos/$Repository/releases?per_page=100&page=$page" -Headers $headers
                $release = $releases | Where-Object { $_.prerelease -and -not $_.draft } |
                    Sort-Object published_at -Descending | Select-Object -First 1
                $page++
            } while (-not $release -and $releases.Count -eq 100)
            if (-not $release) { throw "No published prerelease found for $Repository." }
        } else {
            $release = Invoke-RestMethod -Uri $uri -Headers $headers
        }
        $ReleaseCache[$cacheKey] = $release
        return $release
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

function Get-GitHubAsset([string]$Repository, [string]$Pattern, [string]$CacheName, [switch]$Prerelease) {
    $repoName = ($Repository -split '/')[1]
    $release = Get-GitHubRelease $Repository -Prerelease:$Prerelease
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
    $mm = Get-GitHubAsset 'alliedmodders/metamod-source' $mmPattern "metamod-$($Platform.ToLowerInvariant()).archive" -Prerelease
    $css = Get-GitHubAsset 'roflmuffin/CounterStrikeSharp' $cssPattern "counterstrikesharp-$($Platform.ToLowerInvariant()).archive"
    $extractRoot = Join-Path $DependencyRoot "extracted/$Platform"
    if (Test-Path $extractRoot) { Remove-Item $extractRoot -Recurse -Force }
    $mmRoot = Join-Path $extractRoot 'metamod'
    $cssRoot = Join-Path $extractRoot 'counterstrikesharp'
    Expand-DependencyArchive $mm $mmRoot
    Expand-DependencyArchive $css $cssRoot
    # Copy Metamod's CS2 loader VDFs into the package addons root.
    $metamod = Get-ChildItem $mmRoot -Recurse -Directory -Filter metamod | Select-Object -First 1
    $counterStrikeSharp = Get-ChildItem $cssRoot -Recurse -Directory -Filter counterstrikesharp | Select-Object -First 1
    if (-not $metamod) { throw "Metamod archive did not contain an addons/metamod directory." }
    if (-not $counterStrikeSharp) { throw "CounterStrikeSharp archive did not contain an addons/counterstrikesharp directory." }
    Copy-Tree $metamod.FullName (Join-Path $Destination 'addons/metamod')
    $cs2Binary = if ($windowsPlatform) { 'bin/win64/metamod.2.cs2.dll' } else { 'bin/linuxsteamrt64/metamod.2.cs2.so' }
    if (-not (Test-Path (Join-Path $Destination "addons/metamod/$cs2Binary") -PathType Leaf)) {
        throw "Metamod archive did not contain the CS2 binary: $cs2Binary"
    }
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
    Copy-ComponentFile (Join-Path $DependencyRoot "extracted/$Platform/CS2-Bot-Controller") 'addons/metamod/BotController.vdf' (Join-Path $Destination 'addons/metamod/BotController.vdf')
    Add-ComponentRuntime 'XBribo/CS2-Bot-Controller' '(?i)CSS-API.*\.zip$' 'CS2-Bot-Controller-CSS-API' $Platform $Destination @{
        'addons/counterstrikesharp/shared/BotControllerApi' = 'addons/counterstrikesharp/shared/BotControllerApi'
    }
    Add-ComponentRuntime 'XBribo/CS2-Bot-Hider' $componentPattern 'CS2-Bot-Hider' $Platform $Destination @{
        'addons/BotHider' = 'addons/BotHider'
        'addons/counterstrikesharp/shared/BotHiderApi' = 'addons/counterstrikesharp/shared/BotHiderApi'
        'addons/counterstrikesharp/shared/0Harmony' = 'addons/counterstrikesharp/shared/0Harmony'
    }
    Copy-ComponentFile (Join-Path $DependencyRoot "extracted/$Platform/CS2-Bot-Hider") 'addons/metamod/BotHider.vdf' (Join-Path $Destination 'addons/metamod/BotHider.vdf')
    Add-ComponentRuntime 'XBribo/CS2-Bot-Vision' $componentPattern 'CS2-Bot-Vision' $Platform $Destination @{
        'addons/BotVision' = 'addons/BotVision'
    }
    Copy-ComponentFile (Join-Path $DependencyRoot "extracted/$Platform/CS2-Bot-Vision") 'addons/metamod/BotVision.vdf' (Join-Path $Destination 'addons/metamod/BotVision.vdf')
}

function Add-LocalTemplates([string]$Platform, [string]$Destination) {
    # Use the same game configuration templates for both platforms.
    $templateRoot = Join-Path $PSScriptRoot 'templates/windows'
    $gameInfo = Join-Path $templateRoot 'gameinfo.gi'
    Copy-Item $gameInfo $Destination -Force
    $backup = Join-Path $Destination 'backup'
    New-Item -ItemType Directory -Force -Path $backup | Out-Null
    New-Item -ItemType Directory -Force -Path (Join-Path $backup 'Online'), (Join-Path $backup 'WithBots') | Out-Null
    Copy-Item $gameInfo (Join-Path $backup 'WithBots/gameinfo.gi') -Force
    $onlineGameInfo = Join-Path $backup 'Online/gameinfo.gi'
    Copy-Item $gameInfo $onlineGameInfo -Force
    $content = Get-Content $onlineGameInfo -Raw
    $block = "            Game`tcsgo/overrides/botprofile.vpk`r`n`r`n            Game`tcsgo/addons/metamod`r`n`r`n"
    $content = $content.Replace($block, '')
    [IO.File]::WriteAllText($onlineGameInfo, $content, [Text.UTF8Encoding]::new($false))
    $configRoot = Join-Path $Destination 'addons/counterstrikesharp/configs'
    New-Item -ItemType Directory -Force -Path $configRoot | Out-Null
    Copy-Item (Join-Path $PSScriptRoot 'templates/core.json') (Join-Path $configRoot 'core.json') -Force
    $botHiderRoot = Join-Path $Destination 'addons/BotHider'
    New-Item -ItemType Directory -Force -Path $botHiderRoot | Out-Null
    Copy-Item (Join-Path $PSScriptRoot 'templates/map_whitelist.json') (Join-Path $botHiderRoot 'map_whitelist.json') -Force
}

function Get-ApiProjects() {
    return @(Get-ChildItem (Join-Path $RepoRoot 'addons') -Directory -Filter csharp -Recurse |
        ForEach-Object { Get-ChildItem $_.FullName -Recurse -Filter *.csproj -File } |
        Where-Object {
            $_.BaseName -match '(?i)api$' -and
            $_.FullName.Replace('\', '/') -notmatch '/(disabled|tests|tools|shared)/'
        } | Sort-Object FullName -Unique)
}

function Get-PluginProjects() {
    return @(Get-ChildItem (Join-Path $RepoRoot 'addons') -Recurse -Filter *.csproj -File |
        Where-Object {
            $normalizedPath = $_.FullName.Replace('\', '/')
            $isCounterStrikeSharpPlugin = $normalizedPath -match '/counterstrikesharp/plugins/'
            $isComponentImplementation = $_.BaseName -match '(?i)Impl$'
            $normalizedPath -notmatch '/(disabled|tests|tools|libs)/' -and
            $_.BaseName -ne 'Common' -and
            $_.BaseName -notmatch '(?i)ImplSW\d*$' -and
            ($isCounterStrikeSharpPlugin -or $isComponentImplementation)
        } | Sort-Object FullName -Unique)
}

function Get-BuildOutput([System.IO.FileInfo]$Project) {
    $outputRoot = Join-Path $Project.DirectoryName "bin/$Configuration"
    if (-not (Test-Path $outputRoot)) {
        throw "Build output directory for $($Project.BaseName) was not found: $outputRoot"
    }
    $builtDll = Get-ChildItem $outputRoot -Recurse -Filter "$($Project.BaseName).dll" -File |
        Select-Object -First 1
    if (-not $builtDll) {
        throw "Build output for $($Project.BaseName) was not found under $outputRoot."
    }
    return $builtDll
}

function Build-Plugins([string]$Destination) {
    $runtimeLibraries = @{
        'RayTraceApi.dll' = @{ Repository = 'FUNPLAY-pro-CS2/Ray-Trace' }
        'BotControllerApi.dll' = @{ Repository = 'XBribo/CS2-Bot-Controller' }
    }
    $sharedProjects = Get-ApiProjects
    foreach ($project in $sharedProjects) {
        Write-Host "Building shared API $($project.BaseName)..."
        dotnet build $project.FullName -c $Configuration --nologo
        if ($LASTEXITCODE -ne 0) { throw "dotnet build failed for $($project.FullName)" }
        $builtDll = Get-BuildOutput $project
        $target = Join-Path $Destination "addons/counterstrikesharp/shared/$($project.BaseName)"
        New-Item -ItemType Directory -Force -Path $target | Out-Null
        Copy-Item $builtDll.FullName (Join-Path $target $builtDll.Name) -Force
        foreach ($sidecar in @("$($project.BaseName).deps.json", "$($project.BaseName).pdb")) {
            $file = Join-Path $builtDll.DirectoryName $sidecar
            if (Test-Path $file) { Copy-Item $file (Join-Path $target $sidecar) -Force }
        }
    }

    $projects = Get-PluginProjects
    foreach ($project in $projects) {
        $referenceNames = if ($project.BaseName -in @('BotAimImprover', 'NadeSystem')) { @('RayTraceApi.dll') } elseif ($project.BaseName -eq 'BotState') { @('BotControllerApi.dll') } else { @() }
        foreach ($referenceName in $referenceNames) {
            $reference = $runtimeLibraries[$referenceName]
            $libDir = Join-Path $project.DirectoryName 'libs'
            New-Item -ItemType Directory -Force -Path $libDir | Out-Null
            $localReference = Join-Path $libDir $referenceName
            if ($DownloadDependencies) {
                $source = Get-RepositoryReference $reference.Repository $referenceName
                Copy-Item $source $localReference -Force
            } elseif (-not (Test-Path $localReference)) {
                throw "Building $($project.BaseName) requires '$referenceName'. Re-run with -DownloadDependencies or provide $localReference."
            }
        }
        Write-Host "Building $($project.BaseName)..."
        dotnet build $project.FullName -c $Configuration --nologo
        if ($LASTEXITCODE -ne 0) { throw "dotnet build failed for $($project.FullName)" }
        $target = Join-Path $Destination "addons/counterstrikesharp/plugins/$($project.BaseName)"
        $builtDll = Get-BuildOutput $project
        New-Item -ItemType Directory -Force -Path $target | Out-Null
        Copy-Item $builtDll.FullName (Join-Path $target $builtDll.Name) -Force
        foreach ($sidecar in @("$($project.BaseName).deps.json", "$($project.BaseName).pdb")) {
            $file = Join-Path $builtDll.DirectoryName $sidecar
            if (Test-Path $file) { Copy-Item $file (Join-Path $target $sidecar) -Force }
        }
        $sharedOutput = Join-Path $builtDll.DirectoryName 'shared'
        if (Test-Path $sharedOutput) {
            Copy-Tree $sharedOutput (Join-Path $Destination 'addons/counterstrikesharp/shared')
        }
        $sourceFiles = Get-ChildItem $project.DirectoryName -Recurse -File |
            Where-Object {
                $normalizedPath = $_.FullName.Replace('\', '/')
                $normalizedPath -notmatch '/(\.git|\.github|bin|obj|libs|disabled|tests|tools)/' -and
                $_.Name -ne 'packages.lock.json' -and
                $_.Extension.ToLowerInvariant() -in @('.json', '.kv3', '.vdata', '.vdata_c')
            }
        foreach ($sourceFile in $sourceFiles) {
            $relative = $sourceFile.FullName.Substring($project.DirectoryName.Length).TrimStart([char[]]('/\\'))
            $destinationFile = Join-Path $target $relative
            New-Item -ItemType Directory -Force -Path (Split-Path $destinationFile) | Out-Null
            Copy-Item $sourceFile.FullName $destinationFile -Force
        }
        $externalData = Join-Path $RepoRoot "addons/counterstrikesharp/data/$($project.BaseName)"
        if (Test-Path $externalData) {
            Copy-Tree $externalData $target
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
