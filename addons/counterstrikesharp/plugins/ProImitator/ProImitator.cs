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
    public override string ModuleVersion     => "0.3.0";
    public override string ModuleAuthor      => "Contribution to ed0ard/CS2-Bot-Improver";
    public override string ModuleDescription => "Per-bot personality presets so the donk bot plays like donk";

    // Weapon classifier used by NoCrouchWithRifle and RifleOnly. Anything
    // not in this set (pistols, SMGs, snipers, shotguns) keeps the BotState
    // default crouch behavior so a profiled bot still crouches with e.g. a
    // Deagle pop.
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

    // Sniper classifier used by AwpOnly. AWPers (ZywOo, m0NESY, s1mple) want
    // to stick to their main; Scout / SSG08 included since it's the cheap
    // sniper a pro will pull on pistol rounds.
    private static readonly HashSet<string> SniperDesignerNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "weapon_awp",
        "weapon_ssg08",      // Scout
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

    // Shared RNG for probability-gated traits (e.g. CounterStrafeChance).
    // We don't seed it explicitly — System.Random's time-based seed is fine
    // for "did the human pro mis-time their counter-strafe this round" rolls.
    private readonly Random _rng = new();

    // CounterStrafes state. The pro behavior we're simulating: bot rushes,
    // first sees an enemy (IsAimingAtEnemy transitions false -> true), then
    // we kill its lateral velocity for ~120ms so the first tap lands with
    // CS's full-accuracy "standing still" penalty applied. Real human pros
    // do this via A+D-tap inputs that cancel momentum within 1-3 frames; here
    // we just zero the schema field, same observable effect.
    private readonly Dictionary<int, bool> _wasAimingAtEnemy = new();
    private readonly Dictionary<int, float> _counterStrafeUntil = new();
    private const float CounterStrafeDurationSec = 0.12f;

    // KnifeRush state. The pro round-opener: hold knife to gain ~20% movement
    // speed (260 u/s vs 215 rifle / 200 AWP) during the peaceful rush phase,
    // switch to weapon ~3s before expected contact. We track until-when the
    // bot should hold knife (per-profile KnifeRushSec, set in OnRoundFreezeEnd).
    // Per-tick logic forces knife if currently in window AND not in combat.
    private readonly Dictionary<int, float> _knifeRushUntil = new();

    // V4.1 — Pre/post-plant phase tracker. Set true on EventBombPlanted,
    // reset on EventRoundStart. Read by the BombFocus block in
    // ApplyPersonality to flip attacker/defender intent per side:
    //   pre-plant  : T = attacker, CT = defender
    //   post-plant : T = defender (hold the bomb tick), CT = attacker (retake/defuse)
    //
    // This is the dmarket "post-plant role inversion" — see the V4 banner
    // comment in ApplyPersonality for the full design.
    private bool _bombPlanted = false;

    // -------------------------------------------------------------------------
    public override void Load(bool hotReload)
    {
        LoadProfilesFromDisk();

        RegisterEventHandler<EventPlayerSpawn>(OnPlayerSpawn);
        RegisterEventHandler<EventPlayerTeam>(OnPlayerTeam);
        RegisterEventHandler<EventRoundStart>(OnRoundStart);
        RegisterEventHandler<EventRoundFreezeEnd>(OnRoundFreezeEnd);
        RegisterEventHandler<EventBombPlanted>(OnBombPlanted);
        RegisterListener<Listeners.OnTick>(OnTick);

        // Register commands as plain game console commands (mirrors how the
        // rest of the suite exposes `bot_aim`, `bot_nades`, etc.) so they
        // don't hit the CSS admin permission check that `css_*` commands do
        // by default. Public inspection commands; no destructive operations.
        AddCommand("pro_list",     "List Pro-Imitator profiles loaded from disk",          OnProListCmd);
        AddCommand("pro_assigned", "List bots that currently have a Pro-Imitator profile", OnProAssignedCmd);
        AddCommand("pro_reload",   "Re-read JSON profiles from disk",                       OnProReloadCmd);

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
            _knifeRushUntil.Remove(player.Slot);
        }

        return HookResult.Continue;
    }
    // -------------------------------------------------------------------------
    // KnifeRush window opens when freeze ends — that's when bots actually start
    // moving. We just record per-bot "hold knife until time T"; the actual
    // forced knife switch is done per-tick in ApplyPersonality (so we can
    // gracefully step aside the moment the bot enters a duel).
    //
    // Why not OnRoundStart: freeze can be long (15-20s) and there's no point
    // holding knife while the bot is rooted in spawn. We want the knife hold
    // to start the instant the bot can actually move.
    // -------------------------------------------------------------------------
    [GameEventHandler]
    public HookResult OnRoundFreezeEnd(EventRoundFreezeEnd @event, GameEventInfo info)
    {
        if (_assigned.Count == 0) return HookResult.Continue;

        float now = Server.CurrentTime;
        foreach (var kvp in _assigned)
        {
            if (!kvp.Value.KnifeRush) continue;
            _knifeRushUntil[kvp.Key] = now + kvp.Value.KnifeRushSec;
        }
        return HookResult.Continue;
    }
    // -------------------------------------------------------------------------
    // Role-buy at round-freeze. Strict rule:
    //   if bot has cash >= role weapon's FULL price (no refund credits,
    //   no swap math), buy it.
    //
    // That's the entire "full-buy phase" detector: having the price in cash
    // proves we're in a buy round for this bot. Below the price -> we're on
    // eco / semi-buy / save and ProImitator stays out of it.
    //
    // No fake money anywhere: we never refund the bot's existing weapon and
    // credit them for it. If the bot already owns a different big primary
    // (engine bought him an M4 when his profile is AWPer, or a Galil when
    // his profile is Rifler), we drop that weapon WITHOUT refund and pay
    // the role weapon's full price. The dropped weapon's cost is sunk —
    // the bot clearly had the headroom because they still had the role
    // price in cash AFTER paying for the original.
    //
    // 3.5s delay after EventRoundStart so BotBuy's longest timer (3.0s) has
    // settled. Freeze is typically 15-20s, so we're well inside the buy
    // window for GiveNamedItem to take effect.
    // -------------------------------------------------------------------------
    [GameEventHandler]
    public HookResult OnRoundStart(EventRoundStart @event, GameEventInfo info)
    {
        // Reset pre/post-plant state for the new round so BombFocus reverts
        // to its pre-plant intent (T = attacker, CT = defender).
        _bombPlanted = false;

        if (_assigned.Count == 0) return HookResult.Continue;

        AddTimer(3.5f, BuyRoleWeapons);
        return HookResult.Continue;
    }
    // -------------------------------------------------------------------------
    // EventBombPlanted flips the pre/post-plant flag. Combined with BombFocus
    // in ApplyPersonality, this swaps attacker/defender intent on both sides
    // — T now needs to defend the plant, CT now needs to retake to defuse.
    // See dmarket roles guide → "T-side vs CT-side" section for the rationale.
    // -------------------------------------------------------------------------
    [GameEventHandler]
    public HookResult OnBombPlanted(EventBombPlanted @event, GameEventInfo info)
    {
        _bombPlanted = true;
        return HookResult.Continue;
    }
    // -------------------------------------------------------------------------
    private void BuyRoleWeapons()
    {
        foreach (var kvp in _assigned)
        {
            var player = Utilities.GetPlayerFromSlot(kvp.Key);
            if (player == null || !player.IsValid || !player.IsBot) continue;
            if (player.InGameMoneyServices == null) continue;

            var pawn = player.PlayerPawn.Value;
            if (pawn == null || !pawn.IsValid) continue;

            var prof = kvp.Value;
            bool isT  = player.Team == CsTeam.Terrorist;
            bool isCT = player.Team == CsTeam.CounterTerrorist;
            if (!isT && !isCT) continue;

            // Rifler / AWPer should be mutually exclusive; if both true,
            // Rifler wins by reading first.
            if (prof.Rifler)
            {
                // Prices mirror BotBuy.cs price table (AK 2700, M4A4 2900).
                string preferred = isT ? "weapon_ak47" : "weapon_m4a1";
                int    price     = isT ? 2700        : 2900;
                TryBuyRoleWeapon(player, pawn, preferred, price);
            }
            else if (prof.AWPer)
            {
                TryBuyRoleWeapon(player, pawn, "weapon_awp", 4750);
            }
        }
    }
    // -------------------------------------------------------------------------
    private static void TryBuyRoleWeapon(
        CCSPlayerController player,
        CCSPlayerPawn pawn,
        string preferred,
        int price)
    {
        // Already holds the role weapon? Done.
        if (HasWeapon(pawn, preferred)) return;

        // Strict affordability — bot must have the FULL price in cash. No
        // counting "refund credits" from an existing weapon. This is the
        // single gate that prevents the "ZywOo got AWP without the money"
        // bug from earlier swap-based attempts.
        if (player.InGameMoneyServices!.Account < price) return;

        // Drop any existing big primary that would conflict (engine bought
        // them the "wrong" weapon for the role). Sunk cost — the bot demon-
        // strated by having `price` in cash that they could afford this.
        string? existing = FindOwnedRifle(pawn) ?? FindOwnedSniper(pawn);
        if (existing != null && existing != preferred)
        {
            player.RemoveItemByDesignerName(existing);
        }

        player.GiveNamedItem(preferred);
        player.InGameMoneyServices.Account -= price;
        Utilities.SetStateChanged(player, "CCSPlayerController", "m_pInGameMoneyServices");
    }
    // -------------------------------------------------------------------------
    private static bool HasWeapon(CCSPlayerPawn pawn, string designerName)
    {
        var weapons = pawn.WeaponServices?.MyWeapons;
        if (weapons == null) return false;

        foreach (var handle in weapons)
        {
            var w = handle.Value;
            if (w != null && w.IsValid && w.DesignerName == designerName) return true;
        }
        return false;
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

        // =====================================================================
        // V4 — Objective awareness (BombFocus)
        //
        // Problem this addresses (reported during V3 testing):
        //   Even with role-tuned aggression, CS2 bots default to "engage
        //   whatever enemy I see, wherever I see them". On Dust2 this
        //   produces rounds where Ts and CTs trade kills at mid for 1:45
        //   and the bomb never gets planted. Tactical site execution and
        //   coordinated defence are lost to fragfest behaviour.
        //
        // V4.1 added: pre/post-plant inversion + bomb carrier extra push.
        //
        // V4.2 tone-down: V4.1 was too heavy ("locomotives"). Switched to
        // small Duration values (10-20s) and removed CheckedHidingSpotCount=0
        // override.
        //
        // V4.3 calibration (current): V4.2 was too soft on the defending
        // side and the bomb carrier was indistinguishable from teammates.
        // Three targeted bumps:
        //   - CT (and post-plant T) defenders: HurryTimer EXPLICITLY cleared
        //     (Duration=0, Timestamp=0). Critical because Entry-tagged bots
        //     on the defending side still have AlwaysRushing in their
        //     profile, which writes HurryTimer.Duration=600 every tick. If
        //     BombFocus only writes SafeTime, the AlwaysRushing bot still
        //     rushes — they look "agressifs trop de fight en dehors des BP".
        //     The clear here wins the per-tick race against AlwaysRushing.
        //   - Defender SafeTime raised 3s → 6s. Still not the V4.1 8s, but
        //     more committed-to-position.
        //   - Bomb carrier: HurryTimer 20s → 30s AND IsRunning forced true.
        //     The forced run (no walk) is the visible difference between the
        //     carrier and other Ts, without the V4.1 timer-wipe extremes
        //     (SneakTimer / PoliteTimer / IsWaitingBehindFriend still
        //     untouched — they can still use cover and trade).
        //
        // What BombFocus does per phase (V4.3 values):
        //   PRE-PLANT  T (attacker):  HurryTimer.Duration = 10s, refreshed
        //                             Bomb carrier: HurryTimer 30s + IsRunning
        //              CT (defender): SafeTime = 6s + HurryTimer cleared
        //
        //   POST-PLANT T (defender):  SafeTime = 6s + HurryTimer cleared
        //              CT (attacker): HurryTimer.Duration = 10s, refreshed
        //
        // What BombFocus deliberately does NOT do:
        //   - Override the engine's bombsite assignment. CS2's nav system
        //     decides which bot is "attacker of A vs attacker of B" at
        //     scenario init; we don't touch that. We just nudge the timer
        //     biases so the engine's existing plan executes more cleanly.
        //   - Suppress combat or use of cover. The bot still pauses at
        //     hiding spots, takes peeks, uses utility — BombFocus is a
        //     destination bias, not a behaviour replacement.
        //   - Distribute CTs across A / B / mid. The engine's nav system
        //     already assigns CT bots to sites at scenario init; forcing a
        //     specific 2/2/1 split would fight that assignment. Deferred
        //     future work — would need map-specific bombsite position
        //     lookup.
        //
        // Tuning notes for future maintainers:
        //   The pre/post-plant flag lives in _bombPlanted (set in
        //   OnBombPlanted, reset in OnRoundStart). The bomb carrier is
        //   detected by walking the bot's MyWeapons for weapon_c4 — see
        //   IsCarryingBomb helper. All field writes here are idempotent
        //   and respect the engine's nav assignment.
        //
        //   When tuning numbers: keep them small. V4.1 used 600s and it
        //   looked terrible. V4.2 uses 10-20s and looks like real CS. If
        //   you need more punch, prefer adjusting the AlwaysRushing /
        //   NoSafeTime traits in profiles rather than amplifying these
        //   nudges — those are the heavy levers, BombFocus is the trim.
        // =====================================================================
        if (prof.BombFocus)
        {
            bool isT  = player.Team == CsTeam.Terrorist;
            bool isCT = player.Team == CsTeam.CounterTerrorist;

            // Pre/post-plant phase determines who's the "attacker" (pushing
            // toward objective) and who's the "defender" (holding ground).
            //   pre-plant  → T attacks, CT defends
            //   post-plant → CT attacks (defuse), T defends (the plant)
            bool isAttacker = (isT && !_bombPlanted) || (isCT && _bombPlanted);
            bool isDefender = (isCT && !_bombPlanted) || (isT && _bombPlanted);

            if (isAttacker)
            {
                // Subtle hurry bias: 10s horizon refreshed each tick. Bot
                // prioritises the objective route but the engine still
                // honours hiding-spot pauses, peeks, and cover usage. We
                // deliberately do NOT clear CheckedHidingSpotCount here —
                // that V4.1 line was the "locomotive" behaviour the user
                // flagged as too extreme.
                CountdownTimer hurry = bot.HurryTimer;

                ref float hurryDuration  = ref hurry.Duration;
                hurryDuration            = 10.0f;

                ref float hurryTimestamp = ref hurry.Timestamp;
                hurryTimestamp           = now + 10.0f;

                ref float hurryTimescale = ref hurry.Timescale;
                hurryTimescale           = 1.0f;
            }
            else if (isDefender)
            {
                // Hold bias: SafeTime 6s + HurryTimer EXPLICITLY cleared.
                //
                // The HurryTimer clear is the critical bit. Entry profiles
                // (flameZ on Vitality CT, kyousuke on Falcons CT, makazze
                // on NaVi CT, …) set HurryTimer.Duration=600 every tick via
                // their AlwaysRushing trait. Without an explicit clear here,
                // the defender behaviour gets overwritten and the Entry-
                // tagged CT just rushes mid as if they were still on T.
                // SafeTime alone is not enough to override that.
                ref float safeTime = ref bot.SafeTime;
                safeTime           = 6.0f;

                CountdownTimer hurry = bot.HurryTimer;

                ref float hurryDuration  = ref hurry.Duration;
                hurryDuration            = 0.0f;

                ref float hurryTimestamp = ref hurry.Timestamp;
                hurryTimestamp           = 0.0f;
            }

            // Bomb carrier on pre-plant T-side: more site-focused than the
            // rest of the T side, but still strategic. V4.3 = HurryTimer
            // 30s (vs 10s for other Ts) + IsRunning=true (always runs, no
            // walk). The forced run is the visible "this bot is on
            // mission" signal; the longer hurry horizon biases their path
            // choice toward the assigned site.
            //
            // V4.1's full timer wipe (SneakTimer / PoliteTimer /
            // IsWaitingBehindFriend cleared) stays REMOVED — those wiped
            // away the "strategic" behaviour the user wants the carrier
            // to keep. They still use cover, still wait for trades, still
            // pause at angles; they just run a bit faster toward the site.
            if (isT && !_bombPlanted && IsCarryingBomb(pawn))
            {
                CountdownTimer hurry = bot.HurryTimer;

                ref float hurryDuration  = ref hurry.Duration;
                hurryDuration            = 30.0f;

                ref float hurryTimestamp = ref hurry.Timestamp;
                hurryTimestamp           = now + 30.0f;

                ref bool isRunning = ref bot.IsRunning;
                isRunning = true;
            }
        }

        if (prof.CounterStrafeChance > 0f)
        {
            // Engagement-onset velocity kill. The pro tap-shot rhythm:
            //   rush -> see enemy -> *brief stop* -> first tap (accurate) -> resume
            //
            // We detect the false->true edge of IsAimingAtEnemy. On the edge
            // we roll a die against CounterStrafeChance; on a success we
            // schedule ~120ms of zeroed lateral velocity. Probability < 1.0
            // is what keeps the bot reading as a human: even top pros
            // mis-time their counter-strafe sometimes, and a bot that does
            // it perfectly every single engagement reads as aimbot.
            //
            // Once the window expires, normal movement resumes — sustained
            // spray fights still see the bot strafing.
            bool isAimingAtEnemy = bot.IsAimingAtEnemy;
            bool wasAiming = _wasAimingAtEnemy.GetValueOrDefault(player.Slot, false);

            if (isAimingAtEnemy && !wasAiming)
            {
                if (_rng.NextDouble() <= prof.CounterStrafeChance)
                    _counterStrafeUntil[player.Slot] = now + CounterStrafeDurationSec;
            }

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

        // ---------------------------------------------------------------------
        // KnifeRush: hold knife during the peaceful early-round rush phase
        // (faster movement: 260 u/s vs 215 rifle / 200 AWP) and switch to the
        // role weapon once a real duel is imminent. Window was opened in
        // OnRoundFreezeEnd.
        //
        // "Imminent duel" detection uses two signals:
        //   - bot.IsAimingAtEnemy: the bot's AI has locked onto an enemy.
        //     This is necessary but NOT sufficient — the bot AI sees long
        //     sightlines, and at round-start on Dust2 a CT can lock onto a
        //     T 3000 units away and we don't want that to break the knife
        //     rush across the entire map.
        //   - nearest enemy distance: an alive enemy of the opposite team
        //     must be within KnifeRushCombatRangeUnits (~1500u, roughly mid-
        //     Dust2 length). Below that we treat it as a real duel and pull
        //     the role weapon.
        //
        // Once we decide to force knife, we re-issue `slot3` EVERY tick (no
        // cooldown) until activeWeapon actually becomes a knife — the engine
        // bot AI tries to re-pull primary aggressively and we need to outpace
        // it. `slot3` is more reliable than `use weapon_*` for knives because
        // the designer name varies by team / skin (weapon_knife_t, etc).
        // ---------------------------------------------------------------------
        bool inKnifeRush = _knifeRushUntil.TryGetValue(player.Slot, out float kRushUntil)
                        && now < kRushUntil;

        bool inDuel = false;
        if (inKnifeRush && bot.IsAimingAtEnemy)
        {
            inDuel = NearestEnemyWithinUnits(player, pawn, 1500f);
        }
        bool forceKnife = inKnifeRush && !inDuel;

        if (forceKnife)
        {
            bool holdingKnife = activeWeapon != null && activeWeapon.StartsWith("weapon_knife");
            if (!holdingKnife)
            {
                // V4.1 — we now fire BOTH command paths every tick because
                // playtests showed the client-only `slot3` was getting
                // consistently outraced by the bot AI's internal weapon
                // selection (which runs every server tick too).
                //
                //   1. `slot3`        — client-side slot selection. Cheap,
                //                       maps to whatever knife the bot has.
                //   2. `use weapon_knife` via Server.ExecuteCommand —
                //                       server-side path through the engine's
                //                       weapon-use logic. Different priority
                //                       than slot3 in the command queue, so
                //                       at least one of the two should win
                //                       the race against the AI's auto-select
                //                       on any given tick.
                //
                // No cooldown: re-issue every tick until activeWeapon
                // actually becomes a knife. Once holdingKnife=true the loop
                // stops issuing.
                NativeAPI.IssueClientCommand((int)player.Slot, "slot3");
                Server.ExecuteCommand($"bot_command \"{player.PlayerName}\" use weapon_knife");
            }
        }

        if (prof.Rifler && !forceKnife)
        {
            // Pure preference: if the bot has a rifle in inventory but is
            // currently holding something else (pistol from auto-switch on
            // empty mag, knife, etc), issue `use weapon_<rifle>` to switch
            // back.
            //
            // We never call GiveNamedItem and never touch Account — the
            // engine + BotBuy fully decide what the bot owns. `use weapon_*`
            // only succeeds if the weapon is already in inventory.
            //
            // A 0.5s cooldown prevents spamming the engine: the swap takes
            // a few ticks to resolve, and the next tick after we issue would
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

        if (prof.AWPer && !forceKnife)
        {
            // AWPer mirror of the Rifler switch-back. Same cooldown, same
            // no-give policy: if the engine ever gave the bot an AWP / SSG08
            // and they're holding something else, switch back to it.
            bool holdingSniper = activeWeapon != null && SniperDesignerNames.Contains(activeWeapon);
            if (!holdingSniper
                && (!_lastWeaponSwitchAt.TryGetValue(player.Slot, out float lastAt)
                    || now - lastAt > WeaponSwitchCooldownSec))
            {
                string? sniperToUse = FindOwnedSniper(pawn);
                if (sniperToUse != null)
                {
                    NativeAPI.IssueClientCommand((int)player.Slot, $"use {sniperToUse}");
                    _lastWeaponSwitchAt[player.Slot] = now;
                }
            }
        }
    }
    // -------------------------------------------------------------------------
    // Does the bot's inventory contain the C4? Used by BombFocus to give the
    // bomb carrier an extra push toward planting (no lurk, no wait, run).
    // The CS2 C4 designer name is "weapon_c4"; if it's in MyWeapons the bot
    // is carrying it (only one player per T-side has it at a time).
    // -------------------------------------------------------------------------
    private static bool IsCarryingBomb(CCSPlayerPawn pawn)
    {
        var weapons = pawn.WeaponServices?.MyWeapons;
        if (weapons == null) return false;

        foreach (var handle in weapons)
        {
            var w = handle.Value;
            if (w == null || !w.IsValid) continue;
            if (w.DesignerName == "weapon_c4") return true;
        }
        return false;
    }
    // -------------------------------------------------------------------------
    // Is any alive opposing-team player within maxUnits of selfPawn? Used by
    // KnifeRush to decide whether the bot is in an actual combat-range duel
    // (pull weapon) or just locked on a long-distance enemy across the map
    // (keep knife out).
    //
    // O(N) over all players each tick — N is at most 64 in CS2, negligible.
    // -------------------------------------------------------------------------
    private static bool NearestEnemyWithinUnits(CCSPlayerController self, CCSPlayerPawn selfPawn, float maxUnits)
    {
        if (selfPawn.AbsOrigin == null) return false;
        CsTeam selfTeam = self.Team;
        float maxSq = maxUnits * maxUnits;
        float sx = selfPawn.AbsOrigin.X;
        float sy = selfPawn.AbsOrigin.Y;
        float sz = selfPawn.AbsOrigin.Z;

        foreach (var p in Utilities.GetPlayers())
        {
            if (p == null || !p.IsValid) continue;
            if (p.Team == selfTeam) continue;
            var ePawn = p.PlayerPawn?.Value;
            if (ePawn == null || !ePawn.IsValid || ePawn.AbsOrigin == null) continue;
            if (ePawn.Health <= 0) continue;

            float dx = sx - ePawn.AbsOrigin.X;
            float dy = sy - ePawn.AbsOrigin.Y;
            float dz = sz - ePawn.AbsOrigin.Z;
            if (dx * dx + dy * dy + dz * dz < maxSq) return true;
        }
        return false;
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
    // Sniper-equivalent of FindOwnedRifle. AWP wins ties: we iterate in the
    // order MyWeapons returns, but pros holding both AWP and Scout would
    // always pick the AWP anyway, and the engine returns the primary first.
    // -------------------------------------------------------------------------
    private static string? FindOwnedSniper(CCSPlayerPawn pawn)
    {
        var weapons = pawn.WeaponServices?.MyWeapons;
        if (weapons == null) return null;

        foreach (var handle in weapons)
        {
            var weapon = handle.Value;
            if (weapon == null || !weapon.IsValid) continue;

            string? designer = weapon.DesignerName;
            if (designer != null && SniperDesignerNames.Contains(designer))
                return designer;
        }
        return null;
    }
    // -------------------------------------------------------------------------
    // Console commands. Exposed via AddCommand (not the `[ConsoleCommand]`
    // attribute with a `css_*` name) so they behave like the suite's other
    // user-facing commands (`bot_aim`, `bot_nades`) instead of hitting the CSS
    // admin permission check. All commands are read-only / informational.
    // -------------------------------------------------------------------------
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
            string role    = string.IsNullOrWhiteSpace(prof.Role) ? "" : $"  [{prof.Role}]";
            cmd.ReplyToCommand($"  - {prof.Name}{role}  matches: [{aliases}]");
        }
    }
    // -------------------------------------------------------------------------
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

    // Display-only role tag. Used by `pro_list` to remind operators which
    // bot fills which slot ("Entry rifler", "Lurker", "AWPer", "IGL", ...).
    // Does NOT influence behavior — the trait flags below do that. The point
    // is to make a 5-bot roster legible at a glance and to encourage profiles
    // that double down on a role rather than picking random traits.
    public string Role { get; set; } = "";

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

    // "I main rifles." Two effects:
    //   1. At round-freeze: if the bot has the full price in cash (AK 2700
    //      for T, M4A4 2900 for CT) AND doesn't already own the preferred
    //      rifle, buy it. Any other big primary the engine gave them is
    //      dropped (sunk cost). No refunds, no fake money — strict "buy
    //      iff you have the cash".
    //   2. Per tick: if the bot is holding a non-rifle but already owns one,
    //      `use weapon_*` switches them back to it.
    public bool Rifler               { get; set; } = false;

    // AWPer mirror of Rifler. Same strict "buy if you have 4750 in cash"
    // at round-freeze for the AWP; same per-tick switch-back to a sniper if
    // currently held weapon is something else.
    public bool AWPer                { get; set; } = false;

    // Counter-strafe at engagement onset, gated by probability so the bot
    // doesn't read as aimbot. On the false->true edge of IsAimingAtEnemy
    // we roll a die against this chance; on a success we zero lateral
    // velocity for ~120ms (CounterStrafeDurationSec in ProImitator.cs).
    //
    // 0.0 = never counter-strafe (run-and-gun all engagements).
    // 1.0 = always counter-strafe (perfect, reads as aimbot).
    // 0.7 ~ 0.8 = pro-level: clean most of the time, occasionally misses
    //             the timing like a real human.
    public float CounterStrafeChance { get; set; } = 0f;

    // V4 — Objective awareness. See the V4 banner block in ApplyPersonality
    // for the full design discussion (problem, intent, limitations).
    //
    // T-side effect:  bot HurryTimer maxed, CheckedHidingSpotCount cleared.
    //                 Pushes bot toward its assigned bombsite, less stopping
    //                 for off-route duels.
    // CT-side effect: bot SafeTime extended to 8s. Bot holds its assigned
    //                 site rather than peeking out toward mid.
    //
    // Recommended for: Entry Fraggers, IGLs, Supports — roles that should
    // execute the team plan. Leave OFF for Lurkers and AWPers (they play
    // away from the main objective by design).
    //
    // Don't combine with NoSafeTime=true on the same profile: NoSafeTime
    // forces SafeTime=0 every tick, BombFocus writes SafeTime=8 on CT-side
    // every tick — the two would fight per-tick (last-write-wins) and the
    // bot oscillates between alert and relaxed.
    public bool BombFocus { get; set; } = false;

    // KnifeRush: hold knife during the peaceful early-round rush so the bot
    // gets the knife's movement bonus (~20% faster: 260 u/s vs 215 rifle,
    // 200 AWP). Switches back to the role weapon either after KnifeRushSec
    // elapses OR the moment the bot enters a duel (whichever comes first).
    //
    // Real pros do this all the time on de_*: jog out with the knife,
    // switch ~3s before expected contact. The "expected contact" varies by
    // map and role — entry T sees enemies sooner than a CT AWPer holding
    // a long angle — so KnifeRushSec is per-profile.
    public bool  KnifeRush    { get; set; } = false;
    public float KnifeRushSec { get; set; } = 5.0f;
}
