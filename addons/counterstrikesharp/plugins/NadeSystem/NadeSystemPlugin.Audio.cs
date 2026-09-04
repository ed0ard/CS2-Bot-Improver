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
    // Emits the same radio user messages and grenade_thrown event as a native throw
    // * Emits native-style grenade radio feedback and throw events
    private void AnnounceGrenadeThrow(CCSPlayerController bot, string gtype)
    {
        try
        {
            bool isCT = bot.TeamNum == (int)CsTeam.CounterTerrorist;
            (string RadioText, string Weapon) feedback = gtype switch
            {
                "smoke" => ("#SFUI_TitlesTXT_Smoke_in_the_hole", "smokegrenade"),
                "flash" => ("#SFUI_TitlesTXT_Flashbang_in_the_hole", "flashbang"),
                "he" => ("#SFUI_TitlesTXT_Fire_in_the_hole", "hegrenade"),
                "molotov" when isCT => ("#SFUI_TitlesTXT_Incendiary_in_the_hole", "incgrenade"),
                "molotov" => ("#SFUI_TitlesTXT_Molotov_in_the_hole", "molotov"),
                "incgrenade" => ("#SFUI_TitlesTXT_Incendiary_in_the_hole", "incgrenade"),
                "decoy" => ("#SFUI_TitlesTXT_Decoy_in_the_hole", "decoy"),
                _ => default,
            };

            if (feedback == default)
                return;

            try
            {
                EmitGrenadeThrowSound(bot, gtype);
            }
            catch (Exception ex)
            {
                Server.PrintToConsole(
                    $"[NadeSystem] Grenade throw sound error: {ex.Message}");
            }

            bool ignoreGrenadeRadio =
                ConVar.Find("sv_ignoregrenaderadio")?.GetPrimitiveValue<bool>() ?? false;
            if (!ignoreGrenadeRadio)
            {
                try
                {
                    string radioSound = ResolveGrenadeRadioSound(bot, gtype);
                    SendNativeGrenadeRadio(bot, radioSound, feedback.RadioText);
                }
                catch (Exception ex)
                {
                    Server.PrintToConsole(
                        $"[NadeSystem] Grenade radio error: {ex.Message}");
                }
            }

            var grenadeThrown = new EventGrenadeThrown(true)
            {
                Userid = bot,
                Weapon = feedback.Weapon,
            };
            grenadeThrown.FireEvent(false);
        }
        catch (Exception ex)
        {
            Server.PrintToConsole(
                $"[NadeSystem] AnnounceGrenadeThrow error: {ex.Message}");
        }
    }

    // Emits the current CS2 grenade release event to nearby players on both teams
    // * Plays a nearby grenade release sound for valid recipients
    private void EmitGrenadeThrowSound(CCSPlayerController bot, string grenadeType)
    {
        var pawn = bot.PlayerPawn?.Value;
        if (pawn == null || !pawn.IsValid || !bot.PawnIsAlive)
            return;

        string soundEvent = ResolveGrenadeThrowSound(bot, grenadeType);
        if (string.IsNullOrEmpty(soundEvent))
            return;

        var recipients = BuildGrenadeThrowSoundRecipients(pawn);
        pawn.EmitSound(soundEvent, recipients, 1.0f, 1.0f);
    }

    // Resolves the native throw sound event for the replayed grenade type
    // * Resolves the release sound for a grenade and team
    private static string ResolveGrenadeThrowSound(
        CCSPlayerController bot,
        string grenadeType)
    {
        return grenadeType switch
        {
            "flash" => "Flashbang.Throw",
            "smoke" => "SmokeGrenade.Throw",
            "he" => "HEGrenade.Throw",
            "molotov" when bot.TeamNum == (int)CsTeam.CounterTerrorist
                => "IncGrenade.Throw",
            "molotov" => "Molotov.Throw",
            "incgrenade" => "IncGrenade.Throw",
            "decoy" => "Decoy.Throw",
            _ => "",
        };
    }

    // Builds an all-team recipient filter matching the native throw event range
    // * Builds the distance-limited recipients for throw sounds
    private RecipientFilter BuildGrenadeThrowSoundRecipients(CCSPlayerPawn source)
    {
        var recipients = new RecipientFilter();
        var sourceOrigin = source.AbsOrigin;
        if (sourceOrigin == null)
            return recipients;

        float rangeSquared = GrenadeThrowSoundRange * GrenadeThrowSoundRange;
        foreach (var listener in Utilities.GetPlayers())
        {
            if (!listener.IsValid || listener.IsHLTV)
                continue;

            var listenerOrigin = GetActiveLivePawn(listener)?.AbsOrigin;
            if (listenerOrigin == null)
                continue;

            float dx = listenerOrigin.X - sourceOrigin.X;
            float dy = listenerOrigin.Y - sourceOrigin.Y;
            float dz = listenerOrigin.Z - sourceOrigin.Z;
            if (dx * dx + dy * dy + dz * dz <= rangeSquared)
                recipients.Add(listener);
        }

        return recipients;
    }

    // Resolves a playable grenade voice event for the bot's current agent
    // * Resolves the agent voice event for a grenade radio call
    private static string ResolveGrenadeRadioSound(
        CCSPlayerController bot,
        string grenadeType)
    {
        var pawn = bot.PlayerPawn?.Value;
        if (pawn == null || !pawn.IsValid)
            return "";

        string profile = ResolveRadioVoiceProfile(bot, pawn);
        if (!GrenadeVoiceEvents.TryGetValue(profile, out var voice))
        {
            profile = bot.TeamNum == (int)CsTeam.CounterTerrorist
                ? "fbihrt"
                : "phoenix";
            voice = GrenadeVoiceEvents[profile];
        }

        return grenadeType switch
        {
            "he" => voice.He,
            "flash" => voice.Flash,
            "smoke" => voice.Smoke,
            "molotov" when bot.TeamNum == (int)CsTeam.CounterTerrorist
                => voice.Incendiary,
            "molotov" => voice.Molotov,
            "incgrenade" => voice.Incendiary,
            "decoy" => voice.Decoy,
            _ => "",
        };
    }

    // Resolves the response-rule voice profile from agent definition and model
    // * Resolves the active radio voice profile from agent metadata
    private static string ResolveRadioVoiceProfile(
        CCSPlayerController bot,
        CCSPlayerPawn pawn)
    {
        if (AgentVoiceProfiles.TryGetValue(pawn.CharacterDefIndex, out string? profile))
            return profile;

        var skeleton = pawn.CBodyComponent?.SceneNode?.GetSkeletonInstance();
        string modelName = skeleton?.ModelState.ModelName ?? "";

        if (modelName.Contains("tm_balkan", StringComparison.OrdinalIgnoreCase))
            return "balkan";
        if (modelName.Contains("tm_leet", StringComparison.OrdinalIgnoreCase))
            return "leet";
        if (modelName.Contains("tm_phoenix", StringComparison.OrdinalIgnoreCase))
            return "phoenix";
        if (modelName.Contains("tm_professional", StringComparison.OrdinalIgnoreCase))
            return "professional";
        if (modelName.Contains("tm_jungle", StringComparison.OrdinalIgnoreCase))
            return "jungle_male";
        if (modelName.Contains("ctm_fbi", StringComparison.OrdinalIgnoreCase))
            return "fbihrt";
        if (modelName.Contains("ctm_gsg9", StringComparison.OrdinalIgnoreCase))
            return "gsg9";
        if (modelName.Contains("ctm_sas", StringComparison.OrdinalIgnoreCase))
            return "sas";
        if (modelName.Contains("ctm_st6", StringComparison.OrdinalIgnoreCase))
            return "seal";
        if (modelName.Contains("ctm_swat", StringComparison.OrdinalIgnoreCase))
            return "swat";
        if (modelName.Contains("ctm_gendarmerie", StringComparison.OrdinalIgnoreCase))
            return "gendarmerie_male";
        if (modelName.Contains("ctm_diver", StringComparison.OrdinalIgnoreCase))
            return "seal";

        return bot.TeamNum == (int)CsTeam.CounterTerrorist
            ? "fbihrt"
            : "phoenix";
    }

    // Sends native radio text and plays the agent voice for radio-enabled teammates
    // * Sends native radio text and voice to eligible teammates
    private static void SendNativeGrenadeRadio(
        CCSPlayerController bot,
        string radioSound,
        string radioText)
    {
        var pawn = bot.PlayerPawn?.Value;
        if (pawn == null || !pawn.IsValid || !bot.PawnIsAlive)
            return;

        var recipients = BuildNativeRadioRecipients(bot);
        string location = pawn.LastPlaceName;

        using (var message = UserMessage.FromPartialName("CCSUsrMsg_RadioText"))
        {
            message.SetInt("msg_dst", 3);
            message.SetInt("client", bot.Slot);
            message.SetString(
                "msg_name",
                string.IsNullOrEmpty(location) ? "#Game_radio" : "#Game_radio_location");
            message.AddString("params", bot.PlayerName);

            if (string.IsNullOrEmpty(location))
            {
                message.AddString("params", radioText);
                message.AddString("params", "");
            }
            else
            {
                message.AddString("params", location);
                message.AddString("params", radioText);
            }

            message.AddString("params", "auto");
            message.Send(recipients);
        }

        if (!string.IsNullOrEmpty(radioSound))
            pawn.EmitSound(radioSound, recipients, 1.0f, 1.0f);
    }

    // Builds the standard team-only radio recipient filter
    // * Builds the team-only recipient filter for native radio messages
    private static RecipientFilter BuildNativeRadioRecipients(CCSPlayerController speaker)
    {
        var recipients = new RecipientFilter();
        bool relayRadio = ConVar.Find("tv_relayradio")?.GetPrimitiveValue<bool>() ?? false;

        foreach (var listener in Utilities.GetPlayers())
        {
            if (!listener.IsValid)
                continue;

            if (listener.IsHLTV)
            {
                if (relayRadio)
                    recipients.Add(listener);
                continue;
            }

            if (listener.TeamNum != speaker.TeamNum)
                continue;

            var listenerPawn = listener.PlayerPawn?.Value;
            if (listenerPawn?.IsValid == true &&
                listenerPawn.RadioServices?.IgnoreRadio == true)
            {
                continue;
            }

            recipients.Add(listener);
        }

        return recipients;
    }

}
