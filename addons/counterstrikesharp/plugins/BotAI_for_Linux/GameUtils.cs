using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Memory;

namespace Common;

public static class GameUtils
{
    public static string GetModulePath(string module)
    {
        return module switch
        {
            "server" => Addresses.ServerPath,
            "engine2" => Addresses.EnginePath,
            "tier0" => Addresses.Tier0Path,
            "schemasystem" => Addresses.SchemaSystemPath,
            "vscript" => Addresses.VScriptPath,
            _ => Path.Join(Server.GameDirectory, Constants.RootBinaryPath, $"{Constants.ModulePrefix}{module}{Constants.ModuleSuffix}")
        };
    }

    public static int FindPattern(byte[] buffer, byte[] pattern)
    {
        for (int i = 0; i <= buffer.Length - pattern.Length; i++)
        {
            bool found = true;
            for (int j = 0; j < pattern.Length; j++)
            {
                if (buffer[i + j] != pattern[j])
                {
                    found = false;
                    break;
                }
            }

            if (found)
                return i;
        }

        return -1;
    }
}