<#
.SYNOPSIS
  Active ou desactive ProImitator dans l'install CS2, sans toucher au reste.

.DESCRIPTION
  - 'enable'  : copie ProImitator.dll + profiles/ depuis bin/Release/net8.0/
                vers csgo/addons/counterstrikesharp/plugins/ProImitator/
  - 'disable' : supprime le dossier ProImitator du dossier plugins de CS2
  - 'status'  : indique si le plugin est actuellement installe

  Aucun autre plugin de la suite (BotAI, BotState, BotAimImprover...) n'est
  touche. Le plugin est juste "drop-in / drop-out".

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

# Chemins
$pluginDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$buildOut  = Join-Path $pluginDir "bin\Release\net8.0"
$cs2Plugins = "C:\Program Files (x86)\Steam\steamapps\common\Counter-Strike Global Offensive\game\csgo\addons\counterstrikesharp\plugins"
$cs2Dest   = Join-Path $cs2Plugins "ProImitator"

function Write-Section($msg) {
    Write-Host ""
    Write-Host "=== $msg ===" -ForegroundColor Cyan
}

if (-not (Test-Path $cs2Plugins)) {
    Write-Host "[!!] CS2 plugins folder introuvable : $cs2Plugins" -ForegroundColor Red
    Write-Host "     Verifie que CS2 + CounterStrikeSharp sont installes."
    exit 1
}

switch ($Action) {

    "status" {
        Write-Section "Status ProImitator dans CS2"
        if (Test-Path $cs2Dest) {
            Write-Host "[OK] INSTALLE dans $cs2Dest" -ForegroundColor Green
            Get-ChildItem $cs2Dest -Recurse | ForEach-Object {
                Write-Host "     $($_.FullName.Substring($cs2Dest.Length+1))"
            }
        } else {
            Write-Host "[--] PAS INSTALLE" -ForegroundColor Yellow
            Write-Host "     CS2 utilise les plugins originaux ed0ard/CS2-Bot-Improver uniquement."
        }
    }

    "enable" {
        Write-Section "Enable ProImitator dans CS2"
        if (-not (Test-Path $buildOut)) {
            Write-Host "[!!] Build output introuvable : $buildOut" -ForegroundColor Red
            Write-Host "     Lance d'abord : dotnet build -c Release"
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

        Write-Host "[OK] Installe dans $cs2Dest" -ForegroundColor Green
        Write-Host ""
        Write-Host "Si CS2 tourne, recharge le plugin sans restart serveur :"
        Write-Host "  css_plugins reload ProImitator" -ForegroundColor Yellow
    }

    "disable" {
        Write-Section "Disable ProImitator dans CS2"
        if (-not (Test-Path $cs2Dest)) {
            Write-Host "[--] Pas installe, rien a faire" -ForegroundColor Yellow
            exit 0
        }

        Remove-Item -Recurse -Force $cs2Dest
        Write-Host "[OK] Supprime de $cs2Dest" -ForegroundColor Green
        Write-Host ""
        Write-Host "Les autres plugins (BotAI, BotState, BotAimImprover, ...) sont intacts."
        Write-Host "Si CS2 tourne, unload via console :"
        Write-Host "  css_plugins unload ProImitator" -ForegroundColor Yellow
    }
}
