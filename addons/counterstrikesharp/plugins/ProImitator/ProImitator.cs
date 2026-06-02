using System.Text.Json;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Memory;
using CounterStrikeSharp.API.Modules.Utils;

namespace ProImitator;

// -----------------------------------------------------------------------------
// ProImitator
//
// Layered-on-top personality plugin: scans bot spawn events, matches the bot's
// PlayerName against one of the JSON profiles shipped in `profiles/`, and on
// each tick writes a small set of bot-AI properties that reflect the matched
// pro's playstyle (e.g. always-rushing, never-sneaks, no-panic).
//
// Why "layered on top": this plugin does NOT replace anything in the suite. It
// only sets properties that are either un-touched by BotState or that BotState
// also sets — in which case both writes agree (e.g. PanicTimer = 0). We never
// fight another plugin's intent; we just amplify or specialise it per-bot.
//
// Intentionally minimal V1 scope:
//   - No memory patches  (that's BotAI's domain)
//   - No native hooks    (that's BotAimImprover's domain)
//   - No per-bot aim style override (would require coupling with BotAimImprover)
//   - No pathing / waypoints (we leave CS2's nav-mesh + BotState in charge)
//
// Profile location (read at plugin Load):
//   addons/counterstrikesharp/plugins/ProImitator/profiles/*.json
//
// Identity model:
//   - Bots are spawned via `bot_add_ct "<name>"` (see Commands.txt rosters).
//   - We key everything on `player.Slot` so a single bot keeps its profile for
//     its whole lifetime, even across deaths / respawns within the round.
// -----------------------------------------------------------------------------
public class ProImitator : BasePlugin
{
    public override string ModuleName        => "Pro-Imitator";
    public override string ModuleVersion     => "0.2.0";
    public override string ModuleAuthor      => "Contribution to ed0ard/CS2-Bot-Improver";
    public override string ModuleDescription => "Per-bot personality presets so the donk bot plays like donk";

    // Weapon classifier used by NoCrouchWithRifle. Anything not in this set
    // (pistols, SMGs, snipers, shotguns) keeps the BotState default crouch
    // behavior so a profiled bot still crouches with e.g. a Deagle pop.
    private static readonly HashSet<string> RifleDesignerNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "weapon_ak47",
        "weapon_m4a1",
        "weapon_m4a1_silencer",
        "weapon_sg556",      // Krieg
        "weapon_aug",
        "weapon_galilar",
        "weapon_famas",
    };

    // Loaded profiles, keyed by their JSON 'Name' (lowercased) for de-dup.
    private readonly Dictionary<string, ProProfile> _profiles = new();

    // Bot slot -> applied profile. Survives multiple spawns; cleared on team
    // change so a bot moved to spectator drops its personality cleanly.
    private readonly Dictionary<int, ProProfile> _assigned = new();

    // Cooldown per bot for the RifleOnly weapon switch. Without it we'd send
    // a `use weapon_*` command every tick (64Hz) which spams the engine and
    // prevents the swap from ever resolving.
    private readonly Dictionary<int, float> _lastWeaponSwitchAt = new();
    private const float WeaponSwitchCooldownSec = 0.5f;

    // CounterStrafes state. The pro behavior we're simulating: bot rushes,
    // first sees an enemy (IsAimingAtEnemy transitions false -> true), then
    // we kill its lateral velocity for ~120ms so the first tap lands with
    // CS's full-accuracy "standing still" penalty applied. Real human pros
    // do this via A+D-tap inputs that cancel momentum within 1-3 frames; here
    // we just zero the schema field, same observable effect.
    private readonly Dictionary<int, bool> _wasAimingAtEnemy = new();
    private readonly Dictionary<int, float> _counterStrafeUntil = new();
    private const float CounterStrafeDurationSec = 0.12f;

    // -------------------------------------------------------------------------
    public override void Load(bool hotReload)
    {
        LoadProfilesFromDisk();

        RegisterEventHandler<EventPlayerSpawn>(OnPlayerSpawn);
        RegisterEventHandler<EventPlayerTeam>(OnPlayerTeam);
        RegisterListener<Listeners.OnTick>(OnTick);

        Console.WriteLine($"[Pro-Imitator] loaded with {_profiles.Count} profile(s): "
                          + string.Join(", ", _profiles.Values.Select(p => p.Name)));
    }
    // -------------------------------------------------------------------------
    private void LoadProfilesFromDisk()
    {
        _profiles.Clear();

        string dir = Path.Combine(ModuleDirectory, "profiles");
        if (!Directory.Exists(dir))
        {
            Console.WriteLine($"[Pro-Imitator] profiles dir not found: {dir}");
            return;
        }

        var opts = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        };

        foreach (string path in Directory.EnumerateFiles(dir, "*.json"))
        {
            string filename = Path.GetFileName(path);
            // Profiles starting with '_' are templates/examples; skip silently.
            if (filename.StartsWith('_')) continue;

            try
            {
                string json = File.ReadAllText(path);
                var prof = JsonSerializer.Deserialize<ProProfile>(json, opts);
                if (prof == null || string.IsNullOrWhiteSpace(prof.Name))
                {
                    Console.WriteLine($"[Pro-Imitator] skip {filename}: missing Name");
                    continue;
                }
                _profiles[prof.Name.ToLowerInvariant()] = prof;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Pro-Imitator] failed to parse {filename}: {ex.Message}");
            }
        }
    }
    // -------------------------------------------------------------------------
    // Spawn / team transitions decide who gets a profile attached.
    // -------------------------------------------------------------------------
    [GameEventHandler]
    public HookResult OnPlayerSpawn(EventPlayerSpawn @event, GameEventInfo info)
    {
        var player = @event.Userid;
        if (player == null || !player.IsValid || !player.IsBot) return HookResult.Continue;

        // First spawn for this slot? Attempt to match. Subsequent respawns keep
        // the same profile (we never re-evaluate mid-match unless team changes).
        if (_assigned.ContainsKey(player.Slot)) return HookResult.Continue;

        var prof = MatchProfile(player.PlayerName);
        if (prof != null)
        {
            _assigned[player.Slot] = prof;
            Console.WriteLine($"[Pro-Imitator] attached profile '{prof.Name}' to bot '{player.PlayerName}' (slot {player.Slot})");
        }

        return HookResult.Continue;
    }
    // -------------------------------------------------------------------------
    [GameEventHandler]
    public HookResult OnPlayerTeam(EventPlayerTeam @event, GameEventInfo info)
    {
        var player = @event.Userid;
        if (player == null || !player.IsValid) return HookResult.Continue;

        // Spectator / unassigned: drop the profile so re-joining as a bot lets
        // us re-evaluate (the controller's PlayerName might be reused).
        if ((CsTeam)@event.Team != CsTeam.CounterTerrorist
            && (CsTeam)@event.Team != CsTeam.Terrorist)
        {
            _assigned.Remove(player.Slot);
            _lastWeaponSwitchAt.Remove(player.Slot);
            _wasAimingAtEnemy.Remove(player.Slot);
            _counterStrafeUntil.Remove(player.Slot);
        }

        return HookResult.Continue;
    }
    // -------------------------------------------------------------------------
    private ProProfile? MatchProfile(string botName)
    {
        if (string.IsNullOrEmpty(botName)) return null;
        string lower = botName.ToLowerInvariant();

        foreach (var prof in _profiles.Values)
        {
            foreach (string candidate in prof.MatchByName)
            {
                if (candidate.Equals(lower, StringComparison.OrdinalIgnoreCase))
                    return prof;
            }
        }
        return null;
    }
    // -------------------------------------------------------------------------
    // Per-tick personality writes. Kept fast: a small dictionary lookup per
    // bot and a handful of ref-property writes when a profile is attached.
    //
    // Note on ordering: CounterStrikeSharp runs plugin tick listeners in load
    // order. Plugins loaded later get the last word. We don't depend on that
    // here — we only set values that BotState either leaves alone (HurryTimer,
    // SneakTimer, SafeTime) or sets to the same value we'd want (PanicTimer=0).
    // -------------------------------------------------------------------------
    private void OnTick()
    {
        if (_assigned.Count == 0) return;

        float now = Server.CurrentTime;

        foreach (var player in Utilities.FindAllEntitiesByDesignerName<CCSPlayerController>("cs_player_controller"))
        {
            if (!player.IsValid || !player.IsBot) continue;
            if (!_assigned.TryGetValue(player.Slot, out var prof)) continue;

            // Don't override anything when a human has taken over the bot.
            if (player.HasBeenControlledByPlayerThisRound) continue;

            var pawn = player.PlayerPawn.Value;
            if (pawn == null || !pawn.IsValid) continue;

            var bot = pawn.Bot;
            if (bot == null) continue;

            // Weapon name is needed by a couple of traits. Cheap to read once.
            string? activeWeapon = pawn.WeaponServices?.ActiveWeapon?.Value?.DesignerName;

            ApplyPersonality(player, bot, pawn, prof, now, activeWeapon);
        }
    }
    // -------------------------------------------------------------------------
    // The core: translate a profile's boolean traits into CCSBot property writes.
    //
    // Each block is opt-in; profiles can mix-and-match. Anything not listed in
    // a profile is left untouched (BotState's generic improvements still apply).
    // -------------------------------------------------------------------------
    private void ApplyPersonality(CCSPlayerController player, CCSBot bot, CCSPlayerPawn pawn, ProProfile prof, float now, string? activeWeapon)
    {
        if (prof.AlwaysRushing)
        {
            // HurryTimer drives bot "I'm in a hurry to reach my goal" behavior:
            // ignores hiding spots, takes shortest paths, less likely to camp.
            CountdownTimer hurryTimer = bot.HurryTimer;

            ref float duration = ref hurryTimer.Duration;
            duration = 600.0f;

            ref float timestamp = ref hurryTimer.Timestamp;
            timestamp = now + 600.0f;

            ref float timescale = ref hurryTimer.Timescale;
            timescale = 1.0f;

            // Reinforce: an aggressive entry never patiently walks. IsRunning
            // is set to true by BotState on stuck/idle paths anyway, but here
            // we make it the default while the bot has hurry intent.
            ref bool isRunning = ref bot.IsRunning;
            isRunning = true;
        }

        if (prof.NeverSneaks)
        {
            // SneakTimer = silent crouch-walk. Zero it so even cautious
            // pathing code can't put us in sneak mode.
            CountdownTimer sneakTimer = bot.SneakTimer;

            ref float sneakDuration = ref sneakTimer.Duration;
            sneakDuration = 0.0f;

            ref float sneakTimestamp = ref sneakTimer.Timestamp;
            sneakTimestamp = 0.0f;

            ref float sneakTimescale = ref sneakTimer.Timescale;
            sneakTimescale = 1.0f;
        }

        if (prof.NeverPolite)
        {
            // PoliteTimer = "I'm waiting for a teammate to pass". Disable for
            // bots whose real-life counterpart pushes through their own team.
            CountdownTimer politeTimer = bot.PoliteTimer;

            ref float politeDuration = ref politeTimer.Duration;
            politeDuration = 0.0f;

            ref float politeTimestamp = ref politeTimer.Timestamp;
            politeTimestamp = 0.0f;

            ref float politeTimescale = ref politeTimer.Timescale;
            politeTimescale = 1.0f;

            ref bool waitingBehindFriend = ref bot.IsWaitingBehindFriend;
            waitingBehindFriend = false;
        }

        if (prof.NoSafeTime)
        {
            // SafeTime = how long after spawn the bot considers the area safe
            // and skips threat checks. BotState already zeroes this on spawn,
            // but we keep writing it each tick so the bot never "feels safe".
            ref float safeTime = ref bot.SafeTime;
            safeTime = 0f;
        }

        if (prof.NoPanic)
        {
            // BotState already zeroes PanicTimer in its OnTick, but doing it
            // ourselves means the personality survives if BotState is unloaded.
            CountdownTimer panicTimer = bot.PanicTimer;

            ref float panicDuration = ref panicTimer.Duration;
            panicDuration = 0.0f;

            ref float panicTimestamp = ref panicTimer.Timestamp;
            panicTimestamp = 0.0f;

            ref float panicTimescale = ref panicTimer.Timescale;
            panicTimescale = 1.0f;
        }

        // ---------------------------------------------------------------------
        // V2 visual-identity traits. These are the markers that make a
        // profiled bot READ as "X player" to a spectator rather than just
        // "generic aggressive bot".
        // ---------------------------------------------------------------------

        if (prof.NoCrouchWithRifle)
        {
            // donk's most iconic visual: stand-spray with rifles instead of
            // the 50% crouch chance BotState picks for AK/M4 classes (see
            // BotState.OnWeaponFire). Forcing IsCrouching=false every tick
            // overrides BotState's per-shot decision when the active weapon
            // is a rifle. Non-rifle weapons keep the BotState default so
            // pistol-pop scenarios still look natural.
            if (activeWeapon != null && RifleDesignerNames.Contains(activeWeapon))
            {
                ref bool isCrouching = ref bot.IsCrouching;
                isCrouching = false;
            }
        }

        if (prof.NeverWaitsBetweenShots)
        {
            // Zero the "next allowed shot" timer so the AI never throttles
            // its fire rate between bursts. BotAI has memory patches in the
            // same family (AttackState_SkipFireRateCheck) for all bots; this
            // is the schema-level equivalent re-applied each tick.
            ref float fireWeaponTimestamp = ref bot.FireWeaponTimestamp;
            fireWeaponTimestamp = 0.0f;

            ref bool isRapidFiring = ref bot.IsRapidFiring;
            isRapidFiring = true;
        }

        if (prof.NoApproachPause)
        {
            // ApproachPoint pauses are the little "wait at the corner" beats
            // a bot does when traversing a path. Hyper-aggressive players
            // skip them — they're already committed before reaching the
            // angle. Zeroing InhibitLookAroundTimestamp lets the aim system
            // immediately scan and engage on arrival.
            ref float inhibitLookAroundTimestamp = ref bot.InhibitLookAroundTimestamp;
            inhibitLookAroundTimestamp = 0.0f;

            ref int checkedHidingSpotCount = ref bot.CheckedHidingSpotCount;
            checkedHidingSpotCount = 0;
        }

        if (prof.CounterStrafes)
        {
            // Engagement-onset velocity kill. The pro tap-shot rhythm:
            //   rush -> see enemy -> *brief stop* -> first tap (accurate) -> resume
            //
            // We detect the false->true edge of IsAimingAtEnemy and schedule
            // ~120ms of zeroed lateral velocity. During that window, even if
            // the bot's pathfinder is still trying to push forward, the
            // velocity write each tick keeps it pinned in place.
            //
            // Once the window expires, normal movement resumes — so this
            // doesn't turn donk into a stationary turret in a sustained
            // spray, just gives him the iconic mid-rush counter-strafe stop
            // on EVERY new engagement.
            bool isAimingAtEnemy = bot.IsAimingAtEnemy;
            bool wasAiming = _wasAimingAtEnemy.GetValueOrDefault(player.Slot, false);

            if (isAimingAtEnemy && !wasAiming)
                _counterStrafeUntil[player.Slot] = now + CounterStrafeDurationSec;

            if (_counterStrafeUntil.TryGetValue(player.Slot, out float until) && now < until)
            {
                // Only stomp velocity if the bot is actually moving — avoids
                // a useless SetStateChanged when he's already at rest.
                if (MathF.Abs(pawn.AbsVelocity.X) > 1f || MathF.Abs(pawn.AbsVelocity.Y) > 1f)
                {
                    pawn.AbsVelocity.X = 0f;
                    pawn.AbsVelocity.Y = 0f;
                    Utilities.SetStateChanged(pawn, "CBaseEntity", "m_vecAbsVelocity");
                }
            }

            _wasAimingAtEnemy[player.Slot] = isAimingAtEnemy;
        }

        if (prof.RifleOnly)
        {
            // Force-prefer rifles when the bot is holding something else but
            // already owns a rifle (typically picked up via BotBuy's
            // coordinated rifle commands, or off the ground).
            //
            // We do NOT call GiveNamedItem here — that would break the game's
            // economy and let the bot have a free rifle every round. Instead
            // we issue a `use weapon_<name>` command in the bot's context,
            // which only succeeds if the weapon is already in inventory.
            //
            // A 0.5s cooldown prevents spamming the engine: the swap takes a
            // few ticks to resolve, and the next tick after we issue would
            // still see the old activeWeapon.
            bool holdingRifle = activeWeapon != null && RifleDesignerNames.Contains(activeWeapon);
            if (!holdingRifle
                && (!_lastWeaponSwitchAt.TryGetValue(player.Slot, out float lastAt)
                    || now - lastAt > WeaponSwitchCooldownSec))
            {
                string? rifleToUse = FindOwnedRifle(pawn);
                if (rifleToUse != null)
                {
                    NativeAPI.IssueClientCommand((int)player.Slot, $"use {rifleToUse}");
                    _lastWeaponSwitchAt[player.Slot] = now;
                }
            }
        }
    }
    // -------------------------------------------------------------------------
    // Walk the bot's weapon inventory and return the designer name of the
    // first rifle found, or null if none. Used by RifleOnly to know what to
    // pass to `use <weapon_*>`.
    // -------------------------------------------------------------------------
    private static string? FindOwnedRifle(CCSPlayerPawn pawn)
    {
        var weapons = pawn.WeaponServices?.MyWeapons;
        if (weapons == null) return null;

        foreach (var handle in weapons)
        {
            var weapon = handle.Value;
            if (weapon == null || !weapon.IsValid) continue;

            string? designer = weapon.DesignerName;
            if (designer != null && RifleDesignerNames.Contains(designer))
                return designer;
        }
        return null;
    }
    // -------------------------------------------------------------------------
    // Console commands. Mirrors the convention used elsewhere in the suite:
    // `css_*` prefix, [ConsoleCommand] + [CommandHelper] attributes, reply via
    // CommandInfo so both server console and client console see the output.
    // -------------------------------------------------------------------------
    [ConsoleCommand("css_pro_list", "List Pro-Imitator profiles loaded from disk")]
    [CommandHelper(minArgs: 0, usage: "", whoCanExecute: CommandUsage.CLIENT_AND_SERVER)]
    public void OnProListCmd(CCSPlayerController? caller, CommandInfo cmd)
    {
        if (_profiles.Count == 0)
        {
            cmd.ReplyToCommand("[Pro-Imitator] no profiles loaded");
            return;
        }

        cmd.ReplyToCommand($"[Pro-Imitator] {_profiles.Count} profile(s):");
        foreach (var prof in _profiles.Values.OrderBy(p => p.Name))
        {
            string aliases = prof.MatchByName.Count > 0 ? string.Join(", ", prof.MatchByName) : "(none)";
            cmd.ReplyToCommand($"  - {prof.Name}  matches: [{aliases}]");
        }
    }
    // -------------------------------------------------------------------------
    [ConsoleCommand("css_pro_assigned", "List bots that currently have a Pro-Imitator profile attached")]
    [CommandHelper(minArgs: 0, usage: "", whoCanExecute: CommandUsage.CLIENT_AND_SERVER)]
    public void OnProAssignedCmd(CCSPlayerController? caller, CommandInfo cmd)
    {
        if (_assigned.Count == 0)
        {
            cmd.ReplyToCommand("[Pro-Imitator] no bots currently profiled");
            return;
        }

        // Build slot -> controller index once, so the per-assignment lookups
        // below are O(1) instead of O(N*M) for verbose servers.
        var bySlot = new Dictionary<int, string>();
        foreach (var p in Utilities.GetPlayers())
        {
            if (p == null || !p.IsValid) continue;
            bySlot[p.Slot] = p.PlayerName;
        }

        cmd.ReplyToCommand($"[Pro-Imitator] {_assigned.Count} bot(s) profiled:");
        foreach (var kvp in _assigned)
        {
            string name = bySlot.TryGetValue(kvp.Key, out var n) ? n : "<gone>";
            cmd.ReplyToCommand($"  slot {kvp.Key}  bot '{name}'  -> profile '{kvp.Value.Name}'");
        }
    }
    // -------------------------------------------------------------------------
    [ConsoleCommand("css_pro_reload", "Re-read JSON profiles from disk and re-evaluate all bots")]
    [CommandHelper(minArgs: 0, usage: "", whoCanExecute: CommandUsage.CLIENT_AND_SERVER)]
    public void OnProReloadCmd(CCSPlayerController? caller, CommandInfo cmd)
    {
        int oldCount = _profiles.Count;
        LoadProfilesFromDisk();
        _assigned.Clear();

        // Re-evaluate every currently-alive bot against the freshly loaded set.
        foreach (var p in Utilities.GetPlayers())
        {
            if (p == null || !p.IsValid || !p.IsBot) continue;
            var prof = MatchProfile(p.PlayerName);
            if (prof != null) _assigned[p.Slot] = prof;
        }

        cmd.ReplyToCommand($"[Pro-Imitator] reloaded: {_profiles.Count} profile(s) (was {oldCount}), re-evaluated {_assigned.Count} bot(s)");
    }
}
// -----------------------------------------------------------------------------
// ProProfile
//
// Bag of personality flags + identity. Mirrors the JSON shape on disk: see
// `profiles/_template.json` for the documented field-by-field reference.
//
// Adding a new trait? Two steps:
//   1. Add the property here (default to a no-op value).
//   2. Add an `if (prof.NewTrait) { ... }` block in ProImitator.ApplyPersonality
//      that translates it into CCSBot ref writes.
// -----------------------------------------------------------------------------
public sealed class ProProfile
{
    // Display name. Shown in logs and in `css_pro_list`.
    public string Name { get; set; } = "";

    // List of bot in-game names (case-insensitive) that should receive this
    // profile when they spawn. Typically the pro's nick exactly as it appears
    // in `bot_add_ct "<nick>"` from Commands.txt.
    public List<string> MatchByName { get; set; } = new();

    // Free-form notes shown nowhere at runtime; just so future maintainers can
    // read a profile and understand the intent without digging through git.
    public string Description { get; set; } = "";

    // -- Behavioral flags. All default to false (= "don't touch") so a profile
    //    can opt into only the traits that match the player.
    public bool AlwaysRushing  { get; set; } = false;
    public bool NeverSneaks    { get; set; } = false;
    public bool NeverPolite    { get; set; } = false;
    public bool NoSafeTime     { get; set; } = false;
    public bool NoPanic        { get; set; } = false;

    // -- V2: visual-identity traits that make the bot READ as the specific
    //    player rather than just "another aggressive bot".
    public bool NoCrouchWithRifle    { get; set; } = false;
    public bool NeverWaitsBetweenShots { get; set; } = false;
    public bool NoApproachPause      { get; set; } = false;

    // Force-switch to a rifle whenever the bot is holding a non-rifle AND has
    // a rifle in inventory. Does NOT give weapons (would break the economy);
    // pair with a coordinated BotBuy of `ak47` / `m4a1` / `aug` etc.
    public bool RifleOnly            { get; set; } = false;

    // Counter-strafe at engagement onset. When the bot first sees an enemy
    // (IsAimingAtEnemy transitions false->true), zero its lateral velocity
    // for ~120ms so the first tap-shot lands with full standing-still
    // accuracy. Pro-rusher signature; without this the bot run-and-guns and
    // ALWAYS sprays which is visually wrong.
    public bool CounterStrafes       { get; set; } = false;
}
