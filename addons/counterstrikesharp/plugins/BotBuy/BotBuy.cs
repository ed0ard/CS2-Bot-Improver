using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Utils;
using CounterStrikeSharp.API.Modules.Cvars;
using CounterStrikeSharp.API.Modules.Timers;
using MatchCore;
using Microsoft.Extensions.Logging;
using System.Collections.Generic;
using System.Linq;

namespace BotBuyPatch;

public sealed class BotBuyPatch : BasePlugin
{
    public override string ModuleName        => "BotBuyPatch";
    public override string ModuleVersion     => "1.0.13";
    public override string ModuleAuthor      => "ed0ard";
    public override string ModuleDescription => "Enable bots to take more buy options";

    private Dictionary<int, int> _botUserIdToIndex = new();
    private int _botIndexCounter = 0;

    private readonly Dictionary<CsTeam, HashSet<int>> _poorPlayersByTeam = new();
    private int _mapGeneration;
    private int _roundGeneration;
    private long _purchaseObserved;
    private long _purchaseReplaced;
    private long _purchaseFailed;

    private Dictionary<int, List<string>> _prevWeapons = new();
    private Dictionary<int, int> _prevMoney = new();
    private Dictionary<int, int> _prevArmor = new();
//----------------------------------------------------------------------------------------------
    public override void Load(bool hotReload)
    {
        RegisterListener<Listeners.OnMapStart>(_ =>
        {
            _mapGeneration++;
            _roundGeneration++;
        });
        RegisterListener<Listeners.OnMapEnd>(() =>
        {
            _mapGeneration++;
            _roundGeneration++;
        });
        PublishPurchaseStatus();
    }

    [GameEventHandler]
    public HookResult OnPlayerConnectFull(EventPlayerConnectFull @event, GameEventInfo info)
    {
        var player = @event.Userid;
        if (player != null && player.IsBot) GetBotIndex(player);
        return HookResult.Continue;
    }

    [GameEventHandler]
    public HookResult OnPlayerDisconnect(EventPlayerDisconnect @event, GameEventInfo info)
    {
        var player = @event.Userid;
        if (player != null) RemoveBot(player);
        return HookResult.Continue;
    }

    [GameEventHandler]// Unlock Refund Restriction
    public HookResult OnPlayerDeath(EventPlayerDeath @event, GameEventInfo info)
    {
        var player = @event.Userid;
        if (player != null && player.IsValid && player.IsBot)
        {
            ClearPreviousInventory(player);
        }
        return HookResult.Continue;
    }

    [GameEventHandler]
    public HookResult OnItemPurchase(EventItemPurchase @event, GameEventInfo info)
    {
        var player = @event.Userid;
        string? purchased = BotBuyPolicy.NormalizeWeaponName(@event.Weapon);
        if (player == null || !player.IsValid || !player.IsBot || !player.UserId.HasValue || purchased == null)
            return HookResult.Continue;
        if (!PurchasingAllowed()) return HookResult.Continue;

        _purchaseObserved++;
        var action = BotBuyPolicy.SelectScopedRifleAction(
            purchased, Random.Shared.NextSingle(), Random.Shared.NextSingle());
        if (action is ScopedRifleAction.Keep or ScopedRifleAction.Ignore)
            return HookResult.Continue;

        int userId = player.UserId.Value;
        ScheduleRound(0.10f, () =>
        {
            var current = ResolvePlayer(userId);
            if (current == null || !HasWeapon(current, purchased)) return;
            string replacement = action switch
            {
                ScopedRifleAction.ReplaceWithM4A4 => "weapon_m4a1",
                ScopedRifleAction.ReplaceWithM4A1S => "weapon_m4a1_silencer",
                _ => "weapon_ak47",
            };
            if (Swap(current, purchased, replacement)) _purchaseReplaced++;
            else _purchaseFailed++;
            PublishPurchaseStatus();
        });
        return HookResult.Continue;
    }

    [GameEventHandler]
    public HookResult OnRoundEnd(EventRoundEnd @event, GameEventInfo info)
    {
        SavePreviousInventory();

        return HookResult.Continue;
    }
//----------------------------------------------------------------------------------------------
    [GameEventHandler]
    public HookResult OnRoundStart(EventRoundStart @event, GameEventInfo info)
    {
        _roundGeneration++;
        LogPurchaseCounters();

        // During managed roster mutation no bot controller is stable. Once the
        // coordinator publishes Ready, run the complete upstream buy pipeline.
        if (PlusManagedPaths.TryResolveCsgoRoot(Server.GameDirectory, out var csgoRoot) &&
            !ManagedMatchRuntimeStore.IsPurchasingAllowed(csgoRoot))
            return HookResult.Continue;
        // Don't Buy on Aim_Rush
        if (Server.MapName == "aim_rush") return HookResult.Continue;

        List<CCSPlayerController> allPlayers = new();
        List<CCSPlayerController> ctBots = new();
        List<CCSPlayerController> tBots = new();

        foreach (var player in Utilities.FindAllEntitiesByDesignerName<CCSPlayerController>("cs_player_controller"))
        {
            if (!player.IsValid) continue;  
            allPlayers.Add(player);

            if (player.Team == CsTeam.CounterTerrorist)
            {
                if (player.IsBot) ctBots.Add(player);
            }
            else if (player.Team == CsTeam.Terrorist)
            {
                if (player.IsBot) tBots.Add(player);
            }
        }
        // Drop Weapons
        _poorPlayersByTeam.Clear();
        var poorCT = allPlayers.Where(p => p.IsValid && p.Team == CsTeam.CounterTerrorist && p.InGameMoneyServices?.Account < 2800)
            .Select(p => p.UserId ?? -1).Where(id => id >= 0).ToHashSet();
        var poorT = allPlayers.Where(p => p.IsValid && p.Team == CsTeam.Terrorist && p.InGameMoneyServices?.Account < 2800)
            .Select(p => p.UserId ?? -1).Where(id => id >= 0).ToHashSet();
        _poorPlayersByTeam[CsTeam.CounterTerrorist] = poorCT;
        _poorPlayersByTeam[CsTeam.Terrorist] = poorT;

        ConVar? botLoadout = ConVar.Find("bot_loadout");
        if (botLoadout != null && !string.IsNullOrEmpty(botLoadout.StringValue))
        {
            return HookResult.Continue;
        }
        // Swap HKP2000
        foreach (var player in allPlayers.Where(p => p.IsValid && p.IsBot))
        {
            // Swap HKP2000
            if (Random.Shared.NextSingle() < 0.8f)
            {
                Swap(player, "weapon_hkp2000", "weapon_usp_silencer");
            }
        }
        // Force Buy
        bool allCtInRange = ctBots.Count > 0 && ctBots.All(p =>
            p.InGameMoneyServices != null && p.InGameMoneyServices.Account > 1000 && p.InGameMoneyServices.Account < 2800);

        bool allTInRange = tBots.Count > 0 && tBots.All(p =>
            p.InGameMoneyServices != null && p.InGameMoneyServices.Account > 1000 && p.InGameMoneyServices.Account < 2800);
        ScheduleRound(0.4f, () =>
        {
            if (allCtInRange)
            {
                float roll = Random.Shared.NextSingle();
                foreach (var bot in CurrentPlayers(CsTeam.CounterTerrorist).Where(p => p.IsBot))
                {
                    if (!bot.IsValid) continue;
                    if (roll < 0.10f)
                    {
                        Swap(bot, "weapon_usp_silencer", "weapon_fiveseven");
                        Swap(bot, "weapon_hkp2000", "weapon_fiveseven");
                    }
                    else if (roll < 0.20f)
                    {
                        Buy(bot, "weapon_mp9");
                    }
                }
            }

            if (allTInRange)
            {
                float roll = Random.Shared.NextSingle();
                foreach (var bot in CurrentPlayers(CsTeam.Terrorist).Where(p => p.IsBot))
                {
                    if (!bot.IsValid) continue;
                    if (roll < 0.10f)
                    {
                        Swap(bot, "weapon_glock", "weapon_tec9");
                    }
                    else if (roll < 0.20f)
                    {
                        Buy(bot, "weapon_mac10");
                    }
                }
            }
        });
        // Don't buy if we have scar20/g3sg1
        foreach (var player in allPlayers.Where(p => p.IsValid && p.IsBot))
        {
            var pawn = player.PlayerPawn.Value;
            if (pawn == null || !pawn.IsValid || pawn.WeaponServices == null) continue;

            var activeWeapon = pawn.WeaponServices.ActiveWeapon.Value;
            if (activeWeapon == null) continue;

            string initialGun = activeWeapon.DesignerName;
            if (initialGun != "weapon_scar20" && initialGun != "weapon_g3sg1") continue;

            int userId = player.UserId ?? -1;
            if (userId < 0) continue;
            ScheduleRound(0.5f, () =>
            {
                var currentPlayer = ResolvePlayer(userId);
                if (currentPlayer == null) return;
                var p2 = currentPlayer.PlayerPawn.Value;
                if (p2 == null || !p2.IsValid || p2.WeaponServices == null) return;

                var currentWeapon = p2.WeaponServices.ActiveWeapon.Value;
                if (currentWeapon == null) return;

                string currentGun = currentWeapon.DesignerName;
                if (currentGun != "weapon_scar20" && currentGun != "weapon_g3sg1")
                {
                    Refund(currentPlayer, currentGun);
                }
            });
        }
        // Preserve upstream AUG distribution: 6% AUG, 47% M4A4, 47% M4A1-S.
        ScheduleRound(0.4f, () =>
        {
            foreach (var p in CurrentPlayers().Where(p => p.IsBot && HasWeapon(p, "weapon_aug")))
            {
                float roll = Random.Shared.NextSingle();
                if (roll < BotBuyPolicy.ScopedRifleKeepChance) continue;
                string replacement = roll < 0.53f ? "weapon_m4a1" : "weapon_m4a1_silencer";
                Swap(p, "weapon_aug", replacement);
            }
        });
        // Swap P90
        ScheduleRound(0.4f, () =>
        {
            foreach (var p in CurrentPlayers())
            {
                if (!p.IsValid) continue;
                var pawn = p.PlayerPawn.Value;
                if (pawn == null || !pawn.IsValid || pawn.WeaponServices == null) continue;

                var weapon = pawn.WeaponServices.ActiveWeapon.Value;
                if (weapon == null || weapon.DesignerName != "weapon_p90") continue;

                float roll = Random.Shared.NextSingle();
                if (roll < 0.3f) Swap(p, "weapon_p90", "weapon_bizon");
                else if (roll < 0.4f) Swap(p, "weapon_p90", "weapon_mp7");
                else if (roll < 0.5f) Swap(p, "weapon_p90", "weapon_mp5sd");
                else if (roll < 0.6f) Swap(p, "weapon_p90", "weapon_ump45");
            }
        });
        // Swap XM1014
        ScheduleRound(0.4f, () =>
        {
            foreach (var p in CurrentPlayers())
            {
                if (!p.IsValid) continue;
                var pawn = p.PlayerPawn.Value;
                if (pawn == null || !pawn.IsValid || pawn.WeaponServices == null) continue;

                var weapon = pawn.WeaponServices.ActiveWeapon.Value;
                if (weapon == null || weapon.DesignerName != "weapon_xm1014") continue;

                float roll = Random.Shared.NextSingle();
                if (roll < 0.5f)
                {
                    Swap(p, "weapon_xm1014", "weapon_negev");
                }
                else if (p.Team == CsTeam.CounterTerrorist && roll < 0.6f)
                {
                    Swap(p, "weapon_xm1014", "weapon_mag7");
                }
                else if (p.Team == CsTeam.Terrorist && roll < 0.65f)
                {
                    Swap(p, "weapon_xm1014", "weapon_sawedoff");
                }
            }
        });
        // Swap SSG08
        ScheduleRound(0.4f, () =>
        {
            if (ConVar.Find("sv_gravity")?.GetPrimitiveValue<float>() == 230f) return;
            foreach (var p in CurrentPlayers())
            {
                if (!p.IsValid) continue;
                var pawn = p.PlayerPawn.Value;
                if (pawn == null || !pawn.IsValid || pawn.WeaponServices == null) continue;

                var weapon = pawn.WeaponServices.ActiveWeapon.Value;
                if (weapon == null || weapon.DesignerName != "weapon_ssg08") continue;

                float roll = Random.Shared.NextSingle();

                if (roll < 0.05f)
                {
                    if (p.Team == CsTeam.CounterTerrorist)
                    {
                        Refund(p, "weapon_usp_silencer");
                        Refund(p, "weapon_hkp2000");
                    }
                    else
                    {
                        Refund(p, "weapon_glock");
                    }
                    Swap(p, "weapon_ssg08", "weapon_deagle");
                }
                else if (roll < 0.45f)
                {
                    if (p.Team == CsTeam.Terrorist)
                        Swap(p, "weapon_ssg08", "weapon_mac10");
                    else
                        Swap(p, "weapon_ssg08", "weapon_mp9");
                }
            }
        });
        // Big Advantage
        ScheduleRound(0.6f, () =>
        {
            if (!IsFirstRoundOfHalf())  
            {
                foreach (var p in CurrentPlayers())
                {
                    if (!p.IsValid) continue;
                    if (p.InGameMoneyServices == null || p.InGameMoneyServices.Account < 5200)
                        continue;

                    var pawn = p.PlayerPawn.Value;
                    if (pawn == null || !pawn.IsValid)
                        continue;

                    var weaponServices = pawn.WeaponServices;
                    if (weaponServices == null)
                        continue;

                    var activeWeapon = weaponServices.ActiveWeapon.Value;
                    if (activeWeapon == null)
                        continue;

                    var currentWeapon = activeWeapon.DesignerName;
                    if (string.IsNullOrEmpty(currentWeapon))
                        continue;

                    float roll = Random.Shared.NextSingle();

                    if (roll < 0.10f)
                    {
                        string newGun = p.Team == CsTeam.CounterTerrorist ? "weapon_scar20" : "weapon_g3sg1";
                        Swap(p, currentWeapon, newGun);
                    }
                    else if (roll < 0.14f)
                    {
                        Swap(p, currentWeapon, "weapon_m249");
                    }
                }
            }
        });
        // Buy Defuser
        ScheduleRound(3.0f, () =>
        {
            foreach (var p in CurrentPlayers(CsTeam.CounterTerrorist))
            {
                if (!p.IsValid) continue;
                if (p.InGameMoneyServices == null) continue;

                bool isPoor = p.UserId is int id && _poorPlayersByTeam[CsTeam.CounterTerrorist].Contains(id);
                // Don't buy defuser if poor // Exception: pistol round with 500 left
                if (isPoor && !(IsFirstRoundOfHalf() && p.InGameMoneyServices.Account == 500))
                    continue;

                if (p.InGameMoneyServices.Account < 400)
                    continue;

                var pawn = p.PlayerPawn.Value;
                if (pawn == null || !pawn.IsValid || pawn.ItemServices == null || pawn.ItemServices.Handle == nint.Zero)
                    continue;

                var itemServices = new CCSPlayer_ItemServices(pawn.ItemServices.Handle);
                if (itemServices.HasDefuser)
                    continue;

                Buy(p, "item_defuser");
            }
        });
        // Don't buy Armor if it's above 40
        ScheduleRound(1.0f, () =>
        {
            if (!IsFirstRoundOfHalf())  
            {
                foreach (var p in CurrentPlayers())
                {
                    if (!p.IsValid) continue;
                    var pawn = p.PlayerPawn.Value;
                    if (pawn == null || !pawn.IsValid) continue;
                    var (_, _, prevArmor) = PreviousInventory(p);

                    if (pawn.ItemServices == null || pawn.ItemServices.Handle == nint.Zero)
                    continue;
                    var itemServices = new CCSPlayer_ItemServices(pawn.ItemServices.Handle);

                    int currentArmor = pawn.ArmorValue;

                    if (prevArmor > 40 && prevArmor <= 99 && currentArmor > 99 && itemServices.HasHelmet)
                    {
                        Refund(p, "item_assaultsuit");
                        p.GiveNamedItem("item_assaultsuit");
                        ref int armorValue = ref pawn.ArmorValue;
                        armorValue = prevArmor;
                        Utilities.SetStateChanged(pawn, "CCSPlayerPawn", "m_ArmorValue");
                    }
                }
            }
        });
        // Special Rounds Buy Assistant
        ScheduleRound(0.4f, () =>
        {
            if (IsFirstRoundOfHalf())
            {
                foreach (var p in CurrentPlayers())
                {
                    if (!p.IsValid || p.InGameMoneyServices == null) continue;
                    int money = p.InGameMoneyServices.Account;
                    float r = Random.Shared.NextSingle();

                    // Comp Pistol Rounds
                    if (money == 800)
                    {
                        if (p.Team == CsTeam.CounterTerrorist)
                        {
                            if (r < 0.50f)  Buy(p, "item_kevlar");    // 50%
                            else if (r < 0.65f) { Swap(p, "weapon_usp_silencer", "weapon_elite"); Swap(p, "weapon_hkp2000", "weapon_elite"); } // 15%
                            else if (r < 0.75f) { Swap(p, "weapon_usp_silencer", "weapon_p250"); Swap(p, "weapon_hkp2000", "weapon_p250"); }   // 10%
                            else if (r < 0.83f) { Swap(p, "weapon_usp_silencer", "weapon_deagle"); Swap(p, "weapon_hkp2000", "weapon_deagle"); } // 8%
                            else if (r < 0.91f) { Swap(p, "weapon_usp_silencer", "weapon_cz75a"); Swap(p, "weapon_hkp2000", "weapon_cz75a"); }   // 8%
                            else if (r < 0.98f) { Swap(p, "weapon_usp_silencer", "weapon_fiveseven"); Swap(p, "weapon_hkp2000", "weapon_fiveseven"); } //7%
                            else if (r < 1.00f) { Swap(p, "weapon_usp_silencer", "weapon_revolver"); Swap(p, "weapon_hkp2000", "weapon_revolver"); } //2%
                        }
                        else
                        {
                            if (r < 0.50f)  Buy(p, "item_kevlar");    // 50%
                            else if (r < 0.65f) Swap(p, "weapon_glock", "weapon_elite"); //15%
                            else if (r < 0.77f) Swap(p, "weapon_glock", "weapon_p250");  //12%
                            else if (r < 0.85f) Swap(p, "weapon_glock", "weapon_deagle");//8%
                            else if (r < 0.87f) Swap(p, "weapon_glock", "weapon_revolver");//2%
                            else if (r < 1.00f) Swap(p, "weapon_glock", "weapon_tec9");//13%
                        }
                    }

                    // Casual Pistol Rounds
                    else if (money == 1000)
                    {
                        if (p.Team == CsTeam.CounterTerrorist)
                        {
                            if (r < 0.20f)  { Swap(p, "weapon_usp_silencer", "weapon_elite"); Swap(p, "weapon_hkp2000", "weapon_elite"); } //20%
                            else if (r < 0.50f) { Swap(p, "weapon_usp_silencer", "weapon_deagle"); Swap(p, "weapon_hkp2000", "weapon_deagle"); } //30%
                            else if (r < 0.65f) { Swap(p, "weapon_usp_silencer", "weapon_cz75a"); Swap(p, "weapon_hkp2000", "weapon_cz75a"); } //15%
                            else if (r < 0.95f) { Swap(p, "weapon_usp_silencer", "weapon_fiveseven"); Swap(p, "weapon_hkp2000", "weapon_fiveseven"); } //30%
                            else if (r < 1.00f) { Swap(p, "weapon_usp_silencer", "weapon_revolver"); Swap(p, "weapon_hkp2000", "weapon_revolver"); } //5%
                        }
                        else
                        {
                            if (r < 0.20f)  Swap(p, "weapon_glock", "weapon_elite"); //20%
                            else if (r < 0.30f) Swap(p, "weapon_glock", "weapon_p250"); //10%
                            else if (r < 0.55f) Swap(p, "weapon_glock", "weapon_deagle");//25%
                            else if (r < 0.60f) Swap(p, "weapon_glock", "weapon_revolver");//5%
                            else if (r < 1.00f) Swap(p, "weapon_glock", "weapon_tec9");//40%
                        }
                    }

                    // First Round in OT
                    else if (money == 10000)
                    {
                        Buy(p, "item_assaultsuit");

                        if (p.Team == CsTeam.CounterTerrorist)
                        {
                            if (r < 0.35f)  Buy(p, "weapon_m4a1");
                            else if (r < 0.70f) Buy(p, "weapon_m4a1_silencer");
                            else if (r < 0.90f) Buy(p, "weapon_awp");
                            else if (r < 1.00f) Buy(p, "weapon_scar20");
                        }
                        else
                        {
                            if (r < 0.70f)  Buy(p, "weapon_ak47");
                            else if (r < 0.90f) Buy(p, "weapon_awp");
                            else if (r < 1.00f) Buy(p, "weapon_g3sg1");
                        }
                    }
                }
            }
        });
        // Drop Weapons
        ScheduleRound(2.0f, () =>
        {
            if (!IsFirstRoundOfHalf())  
            {
                foreach (var team in new[] { CsTeam.CounterTerrorist, CsTeam.Terrorist })
                {
                    if (!_poorPlayersByTeam.TryGetValue(team, out var poor))
                        poor = new HashSet<int>();
                    var currentPlayers = CurrentPlayers();
                    var poorPlayers = currentPlayers
                        .Where(p => p.UserId is int id && poor.Contains(id) && p.InGameMoneyServices != null)
                        .ToList();

                    var richBots = currentPlayers.Where(p => p.Team == team && p.IsBot && p.InGameMoneyServices?.Account >= 2900).ToList();

                    if (poorPlayers.Count == 0 || richBots.Count == 0) continue;

                    var giftedPoor = new HashSet<CCSPlayerController>();

                    var shuffledPoor = poorPlayers.Where(p => !HasPrimaryWeapon(p)).OrderBy(_ => Random.Shared.Next()).ToList();
                    int poorIndex = 0;

                    foreach (var rich in richBots)
                    {
                        if (poorIndex >= shuffledPoor.Count) break;
                        if (rich.InGameMoneyServices == null) continue;

                        int richMoney = rich.InGameMoneyServices.Account;
                        int price = team == CsTeam.CounterTerrorist ? 2900 : 2700;

                        int maxGive = richMoney / price;
                        if (maxGive > 3) maxGive = 3;
                        if (maxGive <= 0) continue;

                        int given = 0;
                        while (given < maxGive && poorIndex < shuffledPoor.Count)
                        {
                            var poorPlayer = shuffledPoor[poorIndex];
                            poorIndex++;

                            if (!poorPlayer.IsValid || giftedPoor.Contains(poorPlayer)) continue;

                            string gun = team == CsTeam.CounterTerrorist
                                ? (Random.Shared.Next(2) == 0 ? "weapon_m4a1_silencer" : "weapon_m4a1")
                                : "weapon_ak47";
                            poorPlayer.GiveNamedItem(gun);
                            giftedPoor.Add(poorPlayer);

                            rich.InGameMoneyServices.Account -= price;
                            if (rich.InGameMoneyServices.Account < 0) rich.InGameMoneyServices.Account = 0;
                            Utilities.SetStateChanged(rich, "CCSPlayerController", "m_pInGameMoneyServices");

                            foreach (var teammate in currentPlayers.Where(p => p.Team == team))
                                teammate.PrintToChat($"{ChatColors.Green}{rich.PlayerName}{ChatColors.Yellow}: {poorPlayer.PlayerName}, I dropped a weapon for ya");
                            given++;
                        }
                    }
                }
            }
        });
        // Armor Gift Cycle: richest non-poor bot buys armor for a random unarmored teammate
        ScheduleRound(2.5f, () =>
        {
            foreach (var team in new[] { CsTeam.CounterTerrorist, CsTeam.Terrorist })
            {
                _poorPlayersByTeam.TryGetValue(team, out var poor);
                var poorSet = poor ?? new HashSet<int>();

                while (true)
                {
                    var currentPlayers = CurrentPlayers();
                    var needArmor = currentPlayers
                        .Where(p => p.IsValid && p.IsBot && p.Team == team
                            && HasPrimaryWeapon(p)
                            && (p.PlayerPawn.Value?.ArmorValue ?? 1) == 0)
                        .ToList();

                    if (needArmor.Count == 0) break;

                    var buyer = currentPlayers
                        .Where(p => p.IsValid && p.IsBot && p.Team == team
                            && !(p.UserId is int id && poorSet.Contains(id))
                            && p.InGameMoneyServices?.Account >= 650)
                        .OrderByDescending(p => p.InGameMoneyServices!.Account)
                        .FirstOrDefault();
                    // No one has enough money anymore
                    if (buyer == null) break;

                    var target = needArmor[Random.Shared.Next(needArmor.Count)];
                    if (!target.IsValid) continue;

                    int buyerMoney = buyer.InGameMoneyServices!.Account;
                    // Terrorist bots only buy full armor
                    if (team == CsTeam.Terrorist && buyerMoney < 1000) break;
                    string item = buyerMoney >= 1000 ? "item_assaultsuit" : "item_kevlar";
                    int price   = buyerMoney >= 1000 ? 1000 : 650;

                    target.GiveNamedItem(item);
                    buyer.InGameMoneyServices.Account -= price;
                    Utilities.SetStateChanged(buyer, "CCSPlayerController", "m_pInGameMoneyServices");
                }
            }
        });
        return HookResult.Continue;
    }

    [GameEventHandler]
    public HookResult OnRoundFreezeEnd(EventRoundFreezeEnd @event, GameEventInfo info)
    {
        ConVar? botLoadout = ConVar.Find("bot_loadout");
        if (botLoadout != null && !string.IsNullOrEmpty(botLoadout.StringValue))
        {
            return HookResult.Continue;
        }

        // Don't save money in the last round of each half
        var ecoLimitCvar = ConVar.Find("bot_eco_limit");
        if (ecoLimitCvar != null)
        {
            if (IsSecondToLastRoundOfHalf())
                Server.ExecuteCommand("bot_eco_limit 0");
            else
                Server.ExecuteCommand("bot_eco_limit 2800");
        }

        foreach (var player in Utilities.FindAllEntitiesByDesignerName<CCSPlayerController>("cs_player_controller"))
        {
            if (!player.IsValid || !player.IsBot)
                continue;

            var pawn = player.PlayerPawn.Value;
            if (pawn == null || !pawn.IsValid)
                continue;

            var bot = pawn.Bot;
            if (bot == null)
                continue;

// Nothing here
        }
        return HookResult.Continue;
    }
//----------------------------------------------------------------------------------------------
    private bool HasPrimaryWeapon(CCSPlayerController player)
    {
        if (!player.IsValid || player.PlayerPawn.Value == null)
            return false;

        var pawn = player.PlayerPawn.Value;
        if (pawn.WeaponServices == null)
            return false;

        var activeWeapon = pawn.WeaponServices.ActiveWeapon;
        if (!activeWeapon.IsValid || activeWeapon.Value == null)
            return false;

        var weaponName = activeWeapon.Value.DesignerName;
        if (string.IsNullOrEmpty(weaponName)) return false;

        return weaponName.StartsWith("weapon_ak") ||
            weaponName.StartsWith("weapon_m4") ||
            weaponName.StartsWith("weapon_aug") ||
            weaponName.StartsWith("weapon_galilar") ||
            weaponName.StartsWith("weapon_famas") ||
            weaponName.StartsWith("weapon_awp") ||
            weaponName.StartsWith("weapon_ssg08") ||
            weaponName.StartsWith("weapon_mp") ||
            weaponName.StartsWith("weapon_ump") ||
            weaponName.StartsWith("weapon_p90") ||
            weaponName.StartsWith("weapon_bizon") ||
            weaponName.StartsWith("weapon_nova") ||
            weaponName.StartsWith("weapon_mag7") ||
            weaponName.StartsWith("weapon_sawedoff") ||
            weaponName.StartsWith("weapon_xm1014") ||
            weaponName.StartsWith("weapon_negev") ||
            weaponName.StartsWith("weapon_m249");
    }

    private void ScheduleRound(float delay, Action callback)
    {
        var generation = new BotCallbackGeneration(_mapGeneration, _roundGeneration);
        AddTimer(delay, () =>
        {
            if (!generation.IsCurrent(_mapGeneration, _roundGeneration) || !PurchasingAllowed())
                return;
            callback();
        }, TimerFlags.STOP_ON_MAPCHANGE);
    }

    private bool PurchasingAllowed() =>
        !PlusManagedPaths.TryResolveCsgoRoot(Server.GameDirectory, out var csgoRoot)
        || ManagedMatchRuntimeStore.IsPurchasingAllowed(csgoRoot);

    private static List<CCSPlayerController> CurrentPlayers(CsTeam? team = null) =>
        Utilities.FindAllEntitiesByDesignerName<CCSPlayerController>("cs_player_controller")
            .Where(player => player.IsValid && (!team.HasValue || player.Team == team.Value))
            .ToList();

    private static CCSPlayerController? ResolvePlayer(int userId) =>
        Utilities.FindAllEntitiesByDesignerName<CCSPlayerController>("cs_player_controller")
            .FirstOrDefault(player => player.IsValid && player.IsBot && player.UserId == userId);

    private static bool HasWeapon(CCSPlayerController player, string designerName)
    {
        var weapons = player.PlayerPawn?.Value?.WeaponServices?.MyWeapons;
        if (weapons == null) return false;
        return weapons.Any(handle => string.Equals(
            handle.Value?.DesignerName,
            designerName,
            StringComparison.OrdinalIgnoreCase));
    }

    private void LogPurchaseCounters()
    {
        if (_purchaseObserved == 0 && _purchaseReplaced == 0 && _purchaseFailed == 0) return;
        Logger.LogInformation(
            "[BotBuy] scoped purchase counters: observed={Observed} replaced={Replaced} failed={Failed}",
            _purchaseObserved,
            _purchaseReplaced,
            _purchaseFailed);
        PublishPurchaseStatus();
    }

    private void PublishPurchaseStatus()
    {
        try
        {
            if (!PlusManagedPaths.TryResolveCsgoRoot(Server.GameDirectory, out var csgoRoot)) return;
            BotRuntimeStatusStore.WritePurchase(
                csgoRoot,
                _purchaseObserved,
                _purchaseReplaced,
                _purchaseFailed);
        }
        catch (Exception error)
        {
            Logger.LogWarning(error, "[BotBuy] Failed to publish purchase status");
        }
    }

//----------------------------------------------------------------------------------------------
    private bool Buy(CCSPlayerController player, string itemName)
    {
        if (!player.IsValid || !player.IsBot || player.InGameMoneyServices == null)
            return false;

        var pawn = player.PlayerPawn.Value;
        if (pawn == null || !pawn.IsValid)
            return false;

        int money = player.InGameMoneyServices.Account;
        bool isCT = player.Team == CsTeam.CounterTerrorist;
        bool isT = player.Team == CsTeam.Terrorist;
        int price = 0;
        bool canBuy = true;
        int armor = pawn.ArmorValue;

        switch (itemName)
        {
            case "item_kevlar":              price = 650; break;
            case "item_assaultsuit":         price = armor > 99 ? 350 : 1000; break;

            case "item_defuser":             price = 400; canBuy = isCT; break;
            case "weapon_taser":             price = 200; break;

            case "weapon_glock":             canBuy = isT; break;
            case "weapon_hkp2000":           canBuy = isCT; break;
            case "weapon_usp_silencer":      canBuy = isCT; break;
            case "weapon_elite":             price = 300;  break;
            case "weapon_p250":              price = 300;  break;
            case "weapon_tec9":              price = 500;  canBuy = isT; break;
            case "weapon_fiveseven":         price = 500;  canBuy = isCT; break;
            case "weapon_deagle":            price = 700;  break;
            case "weapon_cz75a":             price = 500;  break;
            case "weapon_revolver":          price = 600;  break;

            case "weapon_mac10":             price = 1050; canBuy = isT; break;
            case "weapon_mp9":               price = 1250; canBuy = isCT; break;
            case "weapon_mp7":               price = 1500; break;
            case "weapon_mp5sd":             price = 1500; break;
            case "weapon_ump45":             price = 1200; break;
            case "weapon_bizon":             price = 1400; break;   
            case "weapon_p90":               price = 2350; break;

            case "weapon_nova":              price = 1050; break;
            case "weapon_xm1014":           price = 2000; break;
            case "weapon_sawedoff":          price = 1100; canBuy = isT; break;
            case "weapon_mag7":              price = 1300; canBuy = isCT; break;

            case "weapon_galilar":           price = 1800; canBuy = isT; break;
            case "weapon_ak47":              price = 2700; canBuy = isT; break;
            case "weapon_sg556":             price = 3000; canBuy = isT; break;
            case "weapon_famas":             price = 1950; canBuy = isCT; break;
            case "weapon_m4a1":              price = 2900; canBuy = isCT; break;
            case "weapon_m4a1_silencer":     price = 2900; canBuy = isCT; break;
            case "weapon_aug":               price = 3300; canBuy = isCT; break;

            case "weapon_ssg08":             price = 1700; break;
            case "weapon_awp":               price = 4750; break;
            case "weapon_scar20":            price = 5000; canBuy = isCT; break;
            case "weapon_g3sg1":             price = 5000; canBuy = isT; break;

            case "weapon_negev":             price = 1700; break;
            case "weapon_m249":              price = 5200; break;

            default: canBuy = false; break;
        }

        if (!canBuy)
            return false;

        if (money < price)
            return false;

        player.GiveNamedItem(itemName);

        player.InGameMoneyServices.Account -= price;
        Utilities.SetStateChanged(player, "CCSPlayerController", "m_pInGameMoneyServices");

        return true;
    }

    private bool Refund(CCSPlayerController player, string itemName)
    {
        if (!player.IsValid || !player.IsBot || player.InGameMoneyServices == null)
        return false;

        var pawn = player.PlayerPawn.Value;
        if (pawn == null || !pawn.IsValid)
            return false;

        if (!CanRefund(player, itemName))
            return false;

        bool hasItem = false;
        int price = 0;
        bool isCT = player.Team == CsTeam.CounterTerrorist;
        bool isT = player.Team == CsTeam.Terrorist;

        if (itemName.StartsWith("weapon_"))
        {
            hasItem = pawn.WeaponServices != null && pawn.WeaponServices.MyWeapons
                .Any(w => w.Value != null && w.Value.DesignerName == itemName);
        }
        else if (itemName == "item_assaultsuit" || itemName == "item_kevlar")
        {
            hasItem = pawn.ArmorValue > 0;
        }

        if (!hasItem)
            return false;

        bool canRefund = true;

        switch (itemName)
        {
            case "item_kevlar":              price = 650; break;
            case "item_assaultsuit":         price = 1000; break;

            case "weapon_taser":             price = 200; break;

            case "weapon_glock":             canRefund = isT; break;
            case "weapon_hkp2000":           canRefund = isCT; break;
            case "weapon_usp_silencer":      canRefund = isCT; break;
            case "weapon_elite":             price = 300;  break;
            case "weapon_p250":              price = 300;  break;
            case "weapon_tec9":              price = 500;  canRefund = isT; break;
            case "weapon_fiveseven":         price = 500;  canRefund = isCT; break;
            case "weapon_deagle":            price = 700;  break;
            case "weapon_cz75a":             price = 500;  break;
            case "weapon_revolver":          price = 600;  break;

            case "weapon_mac10":             price = 1050; canRefund = isT; break;
            case "weapon_mp9":               price = 1250; canRefund = isCT; break;
            case "weapon_mp7":               price = 1500; break;
            case "weapon_mp5sd":             price = 1500; break;
            case "weapon_ump45":             price = 1200; break;
            case "weapon_bizon":             price = 1400; break;
            case "weapon_p90":               price = 2350; break;

            case "weapon_nova":              price = 1050; break;
            case "weapon_xm1014":            price = 2000; break;
            case "weapon_sawedoff":          price = 1100; canRefund = isT; break;
            case "weapon_mag7":              price = 1300; canRefund = isCT; break;

            case "weapon_galilar":           price = 1800; canRefund = isT; break;
            case "weapon_ak47":              price = 2700; canRefund = isT; break;
            case "weapon_sg556":             price = 3000; canRefund = isT; break;
            case "weapon_famas":             price = 1950; canRefund = isCT; break;
            case "weapon_m4a1":              price = 2900; canRefund = isCT; break;
            case "weapon_m4a1_silencer":     price = 2900; canRefund = isCT; break;
            case "weapon_aug":               price = 3300; canRefund = isCT; break;

            case "weapon_ssg08":             price = 1700; break;
            case "weapon_awp":               price = 4750; break;
            case "weapon_scar20":            price = 5000; canRefund = isCT; break;
            case "weapon_g3sg1":             price = 5000; canRefund = isT; break;

            case "weapon_negev":             price = 1700; break;
            case "weapon_m249":              price = 5200; break;

            default: return false;
        }

        if (!canRefund)
            return false;
        
        if (itemName.StartsWith("weapon_"))
        {
            player.RemoveItemByDesignerName(itemName);
        }
        else if (itemName == "item_assaultsuit" || itemName == "item_kevlar")
        {
            pawn.ArmorValue = 0;
            Utilities.SetStateChanged(pawn, "CCSPlayerPawn", "m_ArmorValue");
        }

        player.InGameMoneyServices.Account += price;
        // Cap at mp_maxmoney
        int maxMoney = ConVar.Find("mp_maxmoney")?.GetPrimitiveValue<int>() ?? 16000;
        if (player.InGameMoneyServices.Account > maxMoney)
            player.InGameMoneyServices.Account = maxMoney;
        Utilities.SetStateChanged(player, "CCSPlayerController", "m_pInGameMoneyServices");

        return true;
    }

    private bool CanRefund(CCSPlayerController player, string itemName)
    {
        if (IsFirstRoundOfHalf()) 
            return true;

        if (!player.IsValid || !player.IsBot)
            return false;

        // Check refund restrictions
        var (prevWeapons, _, _) = PreviousInventory(player);
        return !prevWeapons.Contains(itemName);
    }

    private bool Swap(CCSPlayerController player, string oldItem, string newItem)
    {
        if (!player.IsValid || !player.IsBot || player.InGameMoneyServices == null)
            return false;

        var pawn = player.PlayerPawn.Value;
        if (pawn == null || !pawn.IsValid)
            return false;

        if (!HasWeapon(player, oldItem))
            return false;

        if (!Refund(player, oldItem))
            return false;

        if (!Buy(player, newItem))
        {
            Buy(player, oldItem);
            return false;
        }
        return true;
    }

    private bool IsFirstRoundOfHalf()
    {
        try
        {
            var gameRules = Utilities
                .FindAllEntitiesByDesignerName<CCSGameRulesProxy>("cs_gamerules")
                .FirstOrDefault()?.GameRules;

            if (gameRules == null)
                return false;

            int played = gameRules.TotalRoundsPlayed;
            int maxRounds = ConVar.Find("mp_maxrounds")?.GetPrimitiveValue<int>() ?? 24;
            int otMaxRounds = ConVar.Find("mp_overtime_maxrounds")?.GetPrimitiveValue<int>() ?? 6;

            if (maxRounds <= 0) maxRounds = 24;
            if (otMaxRounds <= 0) otMaxRounds = 6;

            int half = maxRounds / 2;
            int otHalf = otMaxRounds / 2;

            return played == 0
                || played == half
                || played == maxRounds
                || (played > maxRounds && (played - maxRounds) % otHalf == 0);
        }
        catch
        {
            return false;
        }
    }

    private bool IsSecondToLastRoundOfHalf()
    {
        try
        {
            var gameRules = Utilities
                .FindAllEntitiesByDesignerName<CCSGameRulesProxy>("cs_gamerules")
                .FirstOrDefault()?.GameRules;

            if (gameRules == null)
                return false;

            int played = gameRules.TotalRoundsPlayed;
            int maxRounds = ConVar.Find("mp_maxrounds")?.GetPrimitiveValue<int>() ?? 24;
            if (maxRounds <= 0) maxRounds = 24;
            int half = maxRounds / 2;

            return played == half - 2 || played == maxRounds - 2;
        }
        catch { return false; }
    }

    private int GetBotIndex(CCSPlayerController player)
    {
        if (!player.IsValid || !player.IsBot) return -1;
        int userId = player.UserId ?? -1;
        if (userId == -1) return -1;

        if (_botUserIdToIndex.TryGetValue(userId, out int idx)) return idx;
        int newIdx = ++_botIndexCounter;
        _botUserIdToIndex[userId] = newIdx;
        return newIdx;
    }

    private void RemoveBot(CCSPlayerController player)
    {
        if (player == null) return;
        int userId = player.UserId ?? -1;
        if (userId == -1) return;

        if (_botUserIdToIndex.Remove(userId, out int idx))
        {
            _prevWeapons.Remove(idx);
            _prevMoney.Remove(idx);
            _prevArmor.Remove(idx);
        }
    }

    private void SavePreviousInventory()
    {
        if (IsFirstRoundOfHalf())
        {
            _prevWeapons.Clear();
            _prevMoney.Clear();
            _prevArmor.Clear();
            return;
        }

        foreach (var player in Utilities.FindAllEntitiesByDesignerName<CCSPlayerController>("cs_player_controller"))
        {
            if (!player.IsValid || !player.IsBot) continue;
            int idx = GetBotIndex(player);
            if (idx == -1) continue;

            var pawn = player.PlayerPawn.Value;
            if (pawn == null || !pawn.IsValid) continue;

            List<string> weapons = new();
            if (pawn.WeaponServices != null)
            {
                foreach (var wHandle in pawn.WeaponServices.MyWeapons)
                {
                    var w = wHandle.Value;
                    if (w == null) continue;
                    string name = w.DesignerName;
                    if (name == "item_kevlar" || name == "item_assaultsuit" || name == "item_defuser") continue;
                    weapons.Add(name);
                }
            }

            int money = player.InGameMoneyServices?.Account ?? 0;
            int armor = pawn.ArmorValue;

            _prevWeapons[idx] = weapons;
            _prevMoney[idx] = money;
            _prevArmor[idx] = armor;
        }
    }

    private void ClearPreviousInventory(CCSPlayerController player)
    {
        if (!player.IsValid || !player.IsBot)
            return;

        int idx = GetBotIndex(player);
        if (idx == -1) return;

        _prevWeapons.Remove(idx);
        _prevArmor.Remove(idx);
    }

    private (List<string> Weapons, int Money, int Armor) Inventory(CCSPlayerController player)
    {
        List<string> weapons = new();
        int money = 0;
        int armor = 0;

        if (!player.IsValid || !player.IsBot) return (weapons, money, armor);
        int idx = GetBotIndex(player);
        if (idx == -1) return (weapons, money, armor);

        var pawn = player.PlayerPawn.Value;
        if (pawn == null || !pawn.IsValid) return (weapons, money, armor);

        money = player.InGameMoneyServices?.Account ?? 0;
        armor = pawn.ArmorValue;

        if (pawn.WeaponServices != null)
        {
            foreach (var wHandle in pawn.WeaponServices.MyWeapons)
            {
                var w = wHandle.Value;
                if (w == null) continue;
                string name = w.DesignerName;
                if (name == "item_kevlar" || name == "item_assaultsuit" || name == "item_defuser") continue;
                weapons.Add(name);
            }
        }

        return (weapons, money, armor);
    }

    private (List<string> Weapons, int Money, int Armor) PreviousInventory(CCSPlayerController player)
    {
        List<string> weapons = new();
        int money = 0;
        int armor = 0;

        if (!player.IsValid || !player.IsBot) return (weapons, money, armor);
        int idx = GetBotIndex(player);
        if (idx == -1) return (weapons, money, armor);

        if (IsFirstRoundOfHalf()) return (weapons, money, armor);

        if (_prevWeapons.TryGetValue(idx, out var w)) weapons = w;
        if (_prevMoney.TryGetValue(idx, out int m)) money = m;
        if (_prevArmor.TryGetValue(idx, out int a)) armor = a;

        return (weapons, money, armor);
    }
}
//----------------------------------------------------------------------------------------------
