using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Commands;
using System.Text;

namespace CS2_ExecAfter
{
	[MinimumApiVersion(60)]
	public partial class CS2_ExecAfter : BasePlugin
	{
		public override string ModuleName => "CS2_ExecAfter";
		public override string ModuleVersion => "1.0.1";
		public override string ModuleAuthor => "Kus (Modified by ed0ard)";
		public override string ModuleDescription => "Executes a command after server event or a delay. exec_after for help";

		public override void Load(bool hotReload)
		{
			RegisterEventHandlers();
		}

		private void RegisterEventHandlers()
		{
			RegisterListener<Listeners.OnMapStart>(OnMapStartHandler);
			RegisterListener<Listeners.OnMapEnd>(OnMapEndHandler);
			RegisterEventHandler<EventRoundStart>(OnRoundStartHandler);
		}

		private string PluginInfo()
		{
			return $"Plugin: {this.ModuleName} - Version: {this.ModuleVersion} by {this.ModuleAuthor}";
		}

		[ConsoleCommand("exec_after", "List of commands for this plugin")]
		public void ConVarExecAfter(CCSPlayerController? player, CommandInfo command)
		{
			StringBuilder stringBuilder = new StringBuilder();
			stringBuilder.AppendLine(PluginInfo());
			stringBuilder.AppendLine($"exec_after_map_start \"<command>\" - Executes a command after every map start");
			stringBuilder.AppendLine($"exec_after_map_start_once \"<command>\" - Executes a command after the next map start");
			stringBuilder.AppendLine($"exec_after_map_end \"<command>\" - Executes a command every time the map ends");
			stringBuilder.AppendLine($"exec_after_map_end_once \"<command>\" - Executes a command when the map ends");
			stringBuilder.AppendLine($"exec_after_round_start \"<command>\" - Executes a command after every round start");
			stringBuilder.AppendLine($"exec_after_round_start_once \"<command>\" - Executes a command after the next round start");
			stringBuilder.AppendLine($"exec_after_delay <delay> \"<command>\" - Execute a command after a delay");
		}

	}
}