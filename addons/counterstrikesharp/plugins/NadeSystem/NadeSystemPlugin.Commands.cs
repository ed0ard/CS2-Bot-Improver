using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Capabilities;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Cvars;
using CounterStrikeSharp.API.Modules.Events;
using CounterStrikeSharp.API.Modules.Memory.DynamicFunctions;
using CounterStrikeSharp.API.Modules.Timers;
using CounterStrikeSharp.API.Modules.UserMessages;
using CounterStrikeSharp.API.Modules.Utils;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NadeSystem;

public partial class NadeSystemPlugin : BasePlugin
{
    // bot_nades convar
    // * Reads or changes the active bot grenade mode
    private void CmdBotNades(CCSPlayerController? player, CommandInfo info)
    {
        if (info.ArgCount < 2)
        {
            Server.PrintToConsole($"[NadeSystem] bot_nades = {_botNadesMode}");
            return;
        }
        var val = info.GetArg(1).ToLower();
        if (val != "off" && val != "less" && val != "normal" && val != "more" && val != "max")
        {
            Server.PrintToConsole("\x0C[NadeSystem]\x01 Usage: bot_nades <off|less|normal|more|max>");
            return;
        }
        _botNadesMode = val;
        Server.PrintToConsole($"[NadeSystem] bot_nades set to {_botNadesMode}");
    }
}
