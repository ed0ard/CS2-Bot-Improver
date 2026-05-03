using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Admin;

namespace CS2_ExecAfter
{
	public partial class CS2_ExecAfter
	{

		public string? exec_after_round_start;
		public string? exec_after_round_start_once;

		private HookResult OnRoundStartHandler(EventRoundStart eventRoundStart, GameEventInfo info)
		{
			Server.NextFrame(() =>
			{
				if (!string.IsNullOrEmpty(exec_after_round_start))
				{
					Server.ExecuteCommand(exec_after_round_start);
				}
				if (!string.IsNullOrEmpty(exec_after_round_start_once))
				{
					Server.ExecuteCommand(exec_after_round_start_once);
					exec_after_round_start_once = null;
				}
			});
			return HookResult.Continue;
		}


		[ConsoleCommand("exec_after_round_start", "Executes a command after every round start")]
		public void ConVarExecAfterRoundStart(CCSPlayerController? player, CommandInfo command)
		{
			string args = command.ArgString.Trim();
			if (string.IsNullOrEmpty(args))
			{
				return;
			}
			args = StripQuotes(args);
			exec_after_round_start = args;
		}

		[ConsoleCommand("exec_after_round_start_once", "Executes a command after the next round start")]
		public void ConVarExecAfterRoundStartOnce(CCSPlayerController? player, CommandInfo command)
		{
			string args = command.ArgString.Trim();
			if (string.IsNullOrEmpty(args))
			{
				return;
			}
			args = StripQuotes(args);
			exec_after_round_start_once = args;
		}

	}
}
