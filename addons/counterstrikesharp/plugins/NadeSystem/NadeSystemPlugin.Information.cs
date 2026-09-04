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
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NadeSystem;

public partial class NadeSystemPlugin : BasePlugin
{
    // ═══════════════════════════════════════════════════════════
    //  Information system: sound events + vision
    // ═══════════════════════════════════════════════════════════

    // * Records Valve's latest audible state for a player
    private HookResult OnPlayerSound(EventPlayerSound @event, GameEventInfo info)
    {
        if (_botNadesMode == "off") return HookResult.Continue;

        var player = @event.Userid;
        if (player == null || !player.IsValid || @event.Radius <= 0 || @event.Duration <= 0f)
            return HookResult.Continue;

        var origin = GetActiveLivePawn(player)?.AbsOrigin;
        if (origin == null) return HookResult.Continue;

        float radius = @event.Radius;
        _playerSounds[(uint)player.Index] = new PlayerSoundState(
            origin.X,
            origin.Y,
            origin.Z,
            radius * radius,
            Server.CurrentTime + @event.Duration);
        return HookResult.Continue;
    }

    // * Records recent weapon fire for combat checks
    private HookResult OnWeaponFire(EventWeaponFire @event, GameEventInfo info)
    {
        if (_botNadesMode == "off") return HookResult.Continue;

        var p = @event.Userid;
        if (p != null && p.IsValid)
            _botLastFireTime[(uint)p.Index] = Server.CurrentTime;
        return HookResult.Continue;
    }

    // * Checks whether a listener is inside a player's active sound radius
    private bool PlayerMadeAudibleSound(
        CCSPlayerController player,
        CCSPlayerController listener)
    {
        if (!player.IsValid || !listener.IsValid) return false;

        uint index = (uint)player.Index;
        if (!_playerSounds.TryGetValue(index, out var sound)) return false;
        if (Server.CurrentTime > sound.ExpiresAt)
        {
            _playerSounds.Remove(index);
            return false;
        }

        var listenerOrigin = GetActiveLivePawn(listener)?.AbsOrigin;
        if (listenerOrigin == null) return false;

        float dx = listenerOrigin.X - sound.X;
        float dy = listenerOrigin.Y - sound.Y;
        float dz = listenerOrigin.Z - sound.Z;
        return dx * dx + dy * dy + dz * dz <= sound.RadiusSquared;
    }

    // True if the given enemy currently sees the target via the official spotting system.
    // Reads the TARGET's SpottedByMask and checks the enemy's slot bit.
    // Falls back to FOV + RayTrace if the schema read is unavailable.
    // * Checks official spotting state before using geometric vision
    private bool EnemySeesTarget(CCSPlayerController enemy, CCSPlayerController target)
    {
        if (!enemy.IsValid || !target.IsValid) return false;
        var targetPawn = GetActiveLivePawn(target);
        if (targetPawn == null || !targetPawn.IsValid) return false;

        try
        {
            var spotted = targetPawn.EntitySpottedState;
            if (spotted != null)
            {
                int slot = enemy.Slot; // entity index - 1
                if (slot >= 0)
                {
                    var mask = spotted.SpottedByMask; // uint[2]
                    int word = slot / 32;
                    int bit  = slot % 32;
                    if (word >= 0 && word < mask.Length)
                        return (mask[word] & (1u << bit)) != 0;
                }
            }
        }
        catch { /* fall through to geometric check */ }

        // Fallback: FOV + RayTrace from enemy eyes to target eyes.
        return EnemySeesTargetGeometric(enemy, target);
    }

    // Geometric vision fallback.
    // * Performs a field-of-view and line-of-sight vision check
    private bool EnemySeesTargetGeometric(CCSPlayerController enemy, CCSPlayerController target)
    {
        var ep = GetActiveLivePawn(enemy);
        var tp = GetActiveLivePawn(target);
        if (ep?.AbsOrigin == null || ep.EyeAngles == null) return false;
        if (tp?.AbsOrigin == null) return false;

        float eyeX = ep.AbsOrigin.X, eyeY = ep.AbsOrigin.Y, eyeZ = ep.AbsOrigin.Z + 64f;
        float tx = tp.AbsOrigin.X, ty = tp.AbsOrigin.Y, tz = tp.AbsOrigin.Z + 64f;

        float dx = tx - eyeX, dy = ty - eyeY, dz = tz - eyeZ;
        float dist2 = dx * dx + dy * dy + dz * dz;
        if (dist2 > 1300f * 1300f) return false;

        float eYawRad   =  ep.EyeAngles.Y * MathF.PI / 180f;
        float ePitchRad = -ep.EyeAngles.X * MathF.PI / 180f;
        float fwdX = MathF.Cos(ePitchRad) * MathF.Cos(eYawRad);
        float fwdY = MathF.Cos(ePitchRad) * MathF.Sin(eYawRad);
        float fwdZ = MathF.Sin(ePitchRad);

        float yawToT   = MathF.Atan2(dy, dx);
        float eyeYaw   = MathF.Atan2(fwdY, fwdX);
        float deltaYaw = MathF.Abs(MathF.Atan2(MathF.Sin(yawToT - eyeYaw),
                                               MathF.Cos(yawToT - eyeYaw)));
        float pitchToT = MathF.Atan2(dz, MathF.Sqrt(dx * dx + dy * dy));
        float eyePitch = MathF.Atan2(fwdZ, MathF.Sqrt(fwdX * fwdX + fwdY * fwdY));
        float deltaPitch = MathF.Abs(pitchToT - eyePitch);
        if (deltaYaw <= 0.927f && deltaPitch <= MathF.PI / 4f) // Horizontal FOV 106° // Vertical FOV 90°
        {
            var tEye = new Vec3 { X = tx, Y = ty, Z = tz };
            return FlashHasLoS(tEye, eyeX, eyeY, eyeZ);
        }
        return false;
    }

    // General-purpose: does `enemy` have information (vision or sound) on `target`?
    // * Combines sound and vision into an enemy information check
    private bool HasInformationOn(CCSPlayerController enemy, CCSPlayerController target)
    {
        if (PlayerMadeAudibleSound(target, enemy)) return true;
        if (EnemySeesTarget(enemy, target)) return true;
        return false;
    }

    // Recently fired（used to suppress HE/molotov right after shooting）
    // * Checks whether a player fired within the requested interval
    private bool FiredRecently(CCSPlayerController player, float seconds)
    {
        if (_botLastFireTime.TryGetValue((uint)player.Index, out float t))
            return Server.CurrentTime - t < seconds;
        return false;
    }

}
