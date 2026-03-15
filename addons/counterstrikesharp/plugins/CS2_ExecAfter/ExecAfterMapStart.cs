using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Admin;

namespace CS2_ExecAfter
{
	public partial class CS2_ExecAfter
	{

		public string? exec_after_map_start;
		public string? exec_after_map_start_once;

		private void OnMapStartHandler(string mapName)
		{
			Server.NextFrame(() =>
			{
				if (!string.IsNullOrEmpty(exec_after_map_start))
				{
					Server.ExecuteCommand(exec_after_map_start);
				}
				if (!string.IsNullOrEmpty(exec_after_map_start_once))
				{
					Server.ExecuteCommand(exec_after_map_start_once);
					exec_after_map_start_once = null;
				}
			});
		}

		[ConsoleCommand("exec_after_map_start", "Executes a command after every map start")]
		public void ConVarExecAfterMapStart(CCSPlayerController? player, CommandInfo command)
		{
			string args = command.ArgString.Trim();
			if (string.IsNullOrEmpty(args))
			{
				return;
			}
			args = StripQuotes(args);
			exec_after_map_start = args;
		}

		[ConsoleCommand("exec_after_map_start_once", "Executes a command after the next map start")]
		public void ConVarExecAfterMapStartOnce(CCSPlayerController? player, CommandInfo command)
		{
			string args = command.ArgString.Trim();
			if (string.IsNullOrEmpty(args))
			{
				return;
			}
			args = StripQuotes(args);
			exec_after_map_start_once = args;
		}

	}
}