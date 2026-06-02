<#
.SYNOPSIS
  Enable or disable ProImitator inside the CS2 install, without touching
  the rest of the ed0ard/CS2-Bot-Improver suite.

.DESCRIPTION
  - 'enable'  : copies ProImitator.dll + profiles/ from bin/Release/net8.0/
                into csgo/addons/counterstrikesharp/plugins/ProImitator/
  - 'disable' : removes the ProImitator folder from the CS2 plugins dir
  - 'status'  : reports whether the plugin is currently installed

  None of the other plugins in the suite (BotAI, BotState, BotAimImprover,
  BotBuy, BotRandomizer, NadeSystem, RoundDamageRecap) are touched. The
  plugin is a clean drop-in / drop-out.

  Developer convenience script — intentionally hardcoded to a single CS2
  install path (Steam default on Windows). Edit `$cs2Plugins` below if your
  install is elsewhere.

.PARAMETER Action
  enable | disable | status (default: status)

.EXAMPLE
  .\toggle_cs2_install.ps1 enable
  .\toggle_cs2_install.ps1 disable
  .\toggle_cs2_install.ps1 status
#>

param(
    [Parameter(Position = 0)]
    [ValidateSet("enable", "disable", "status")]
    [string]$Action = "status"
)

$ErrorActionPreference = "Stop"

# Paths
$pluginDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$buildOut  = Join-Path $pluginDir "bin\Release\net8.0"
$cs2Plugins = "C:\Program Files (x86)\Steam\steamapps\common\Counter-Strike Global Offensive\game\csgo\addons\counterstrikesharp\plugins"
$cs2Dest   = Join-Path $cs2Plugins "ProImitator"

function Write-Section($msg) {
    Write-Host ""
    Write-Host "=== $msg ===" -ForegroundColor Cyan
}

if (-not (Test-Path $cs2Plugins)) {
    Write-Host "[!!] CS2 plugins folder not found: $cs2Plugins" -ForegroundColor Red
    Write-Host "     Verify that CS2 + CounterStrikeSharp are installed."
    exit 1
}

switch ($Action) {

    "status" {
        Write-Section "ProImitator status in CS2"
        if (Test-Path $cs2Dest) {
            Write-Host "[OK] INSTALLED at $cs2Dest" -ForegroundColor Green
            Get-ChildItem $cs2Dest -Recurse | ForEach-Object {
                Write-Host "     $($_.FullName.Substring($cs2Dest.Length+1))"
            }
        } else {
            Write-Host "[--] NOT INSTALLED" -ForegroundColor Yellow
            Write-Host "     CS2 is running the original ed0ard/CS2-Bot-Improver suite only."
        }
    }

    "enable" {
        Write-Section "Enable ProImitator in CS2"
        if (-not (Test-Path $buildOut)) {
            Write-Host "[!!] Build output not found: $buildOut" -ForegroundColor Red
            Write-Host "     Run first: dotnet build -c Release"
            exit 1
        }

        # Cleanup + recreate
        if (Test-Path $cs2Dest) {
            Remove-Item -Recurse -Force $cs2Dest
        }
        New-Item -ItemType Directory -Force -Path $cs2Dest | Out-Null

        # Copy DLL + deps + pdb + profiles
        Copy-Item -Path (Join-Path $buildOut "ProImitator.dll")       -Destination $cs2Dest
        Copy-Item -Path (Join-Path $buildOut "ProImitator.deps.json") -Destination $cs2Dest
        if (Test-Path (Join-Path $buildOut "ProImitator.pdb")) {
            Copy-Item -Path (Join-Path $buildOut "ProImitator.pdb") -Destination $cs2Dest
        }
        Copy-Item -Recurse -Path (Join-Path $buildOut "profiles") -Destination $cs2Dest -Force

        Write-Host "[OK] Installed at $cs2Dest" -ForegroundColor Green
        Write-Host ""
        Write-Host "If CS2 is running, hot-reload without restarting the server:"
        Write-Host "  css_plugins reload ProImitator" -ForegroundColor Yellow
    }

    "disable" {
        Write-Section "Disable ProImitator in CS2"
        if (-not (Test-Path $cs2Dest)) {
            Write-Host "[--] Not installed, nothing to do" -ForegroundColor Yellow
            exit 0
        }

        Remove-Item -Recurse -Force $cs2Dest
        Write-Host "[OK] Removed from $cs2Dest" -ForegroundColor Green
        Write-Host ""
        Write-Host "The other plugins (BotAI, BotState, BotAimImprover, ...) are intact."
        Write-Host "If CS2 is running, unload via console:"
        Write-Host "  css_plugins unload ProImitator" -ForegroundColor Yellow
    }
}
