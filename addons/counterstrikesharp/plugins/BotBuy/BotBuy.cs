using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Utils;
using CounterStrikeSharp.API.Modules.Cvars;
using CounterStrikeSharp.API.Modules.Memory;
using CounterStrikeSharp.API.Modules.Timers;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using CounterStrikeSharp.API.Modules.Memory.DynamicFunctions;

namespace BotBuyPatch;

public sealed class BotBuyPatch : BasePlugin
{
    public override string ModuleName        => "BotBuyPatch";
    public override string ModuleVersion     => "1.0.6";
    public override string ModuleAuthor      => "ed0ard";
    public override string ModuleDescription => "Enable bots to take more buy options";

    private Dictionary<int, int> _botUserIdToIndex = new();
    private int _botIndexCounter = 0;

    Dictionary<CsTeam, List<CCSPlayerController>> _poorPlayersByTeam = new();

    private Dictionary<int, List<string>> _prevWeapons = new();
    private Dictionary<int, int> _prevMoney = new();
    private Dictionary<int, int> _prevArmor = new();

    public static readonly MemoryFunctionWithReturn<nint, string, CCSWeaponBaseVData> _findWeaponVDataByName =
            new(GameData.GetSignature("FindWeaponVDataByName"));

//----------------------------------------------------------------------------------------------
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
    public HookResult OnRoundEnd(EventRoundEnd @event, GameEventInfo info)
    {
        SavePreviousInventory();

        return HookResult.Continue;
    }
//----------------------------------------------------------------------------------------------
    [GameEventHandler]
    public HookResult OnRoundStart(EventRoundStart @event, GameEventInfo info)
    {
        // Don't Buy on Aim_Rush
        if (Server.MapName == "aim_rush") return HookResult.Continue;

        List<CCSPlayerController> allPlayers = new();
        List<CCSPlayerController> allCT = new();
        List<CCSPlayerController> allT = new();
        List<CCSPlayerController> ctBots = new();
        List<CCSPlayerController> tBots = new();

        foreach (var player in Utilities.FindAllEntitiesByDesignerName<CCSPlayerController>("cs_player_controller"))
        {
            if (!player.IsValid) continue;
            allPlayers.Add(player);

            if (player.Team == CsTeam.CounterTerrorist)
            {
                allCT.Add(player);
                if (player.IsBot) ctBots.Add(player);
            }
            else if (player.Team == CsTeam.Terrorist)
            {
                allT.Add(player);
                if (player.IsBot) tBots.Add(player);
            }
        }
        // Drop Weapons
        _poorPlayersByTeam.Clear();
        var poorCT = allPlayers.Where(p => p.IsValid && p.Team == CsTeam.CounterTerrorist && p.InGameMoneyServices?.Account < 2800).ToList();
        var poorT = allPlayers.Where(p => p.IsValid && p.Team == CsTeam.Terrorist && p.InGameMoneyServices?.Account < 2800).ToList();
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
        AddTimer(0.4f, () =>
        {
            if (allCtInRange)
            {
                float roll = Random.Shared.NextSingle();
                foreach (var bot in ctBots)
                {
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
                foreach (var bot in tBots)
                {
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

            var copyPlayer = player;
            AddTimer(0.45f, () =>
            {
                if (!copyPlayer.IsValid) return;
                var p2 = copyPlayer.PlayerPawn.Value;
                if (p2 == null || !p2.IsValid || p2.WeaponServices == null) return;

                var currentWeapon = p2.WeaponServices.ActiveWeapon.Value;
                if (currentWeapon == null) return;

                string currentGun = currentWeapon.DesignerName;
                if (currentGun != "weapon_scar20" && currentGun != "weapon_g3sg1")
                {
                    Refund(copyPlayer, currentGun);
                }
            });
        }
        // Swap AUG
        foreach (var player in allPlayers.Where(p => p.IsValid && p.IsBot))
        {
            var copyPlayer = player;
            float rand = Random.Shared.NextSingle();

            if (rand < 0.06f)
            {
            }
            else if (rand < 0.53f)
            {
                AddTimer(0.4f, () =>
                {
                    if (!copyPlayer.IsValid) return;
                    Swap(copyPlayer, "weapon_aug", "weapon_m4a1");
                });
            }
            else
            {
                AddTimer(0.4f, () =>
                {
                    if (!copyPlayer.IsValid) return;
                    Swap(copyPlayer, "weapon_aug", "weapon_m4a1_silencer");
                });
            }
        }
        // Swap P90
        AddTimer(0.4f, () =>
        {
            foreach (var p in allPlayers)
            {
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
        AddTimer(0.4f, () =>
        {
            foreach (var p in allPlayers)
            {
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
        AddTimer(0.4f, () =>
        {
            if (ConVar.Find("sv_gravity")?.GetPrimitiveValue<float>() == 230f) return;
            foreach (var p in allPlayers)
            {
                var pawn = p.PlayerPawn.Value;
                if (pawn == null || !pawn.IsValid || pawn.WeaponServices == null) continue;

                var weapon = pawn.WeaponServices.ActiveWeapon.Value;
                if (weapon == null || weapon.DesignerName != "weapon_ssg08") continue;

                float roll = Random.Shared.NextSingle();

                if (roll < 0.15f)
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
                else if (roll < 0.30f)
                {
                    if (p.Team == CsTeam.Terrorist)
                        Swap(p, "weapon_ssg08", "weapon_mac10");
                    else
                        Swap(p, "weapon_ssg08", "weapon_mp9");
                }
            }
        });
        // Big Advantage
        AddTimer(0.5f, () =>
        {
            if (!IsFirstRoundOfHalf())
            {
                foreach (var p in allPlayers)
                {
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
        AddTimer(2.0f, () =>
        {
            foreach (var p in allCT)
            {
                if (!p.IsValid) continue;
                if (p.InGameMoneyServices == null) continue;

                bool isPoor = _poorPlayersByTeam[CsTeam.CounterTerrorist].Contains(p);
                // Don't buy defuser if poor
                if (isPoor && p.InGameMoneyServices.Account != 500)
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
        AddTimer(1.0f, () =>
        {
            if (!IsFirstRoundOfHalf())
            {
                foreach (var p in allPlayers)
                {
                    if (!p.IsValid || p.PlayerPawn.Value == null) continue;

                    var pawn = p.PlayerPawn.Value;
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
        AddTimer(0.4f, () =>
        {
            if (IsFirstRoundOfHalf())
            {
                foreach (var p in allPlayers)
                {
                    if (!p.IsValid || p.InGameMoneyServices == null) continue;
                    int money = p.InGameMoneyServices.Account;
                    float r = Random.Shared.NextSingle();

                    // Comp Pistol Rounds
                    if (money == 800)
                    {
                        if (p.Team == CsTeam.CounterTerrorist)
                        {
                            if (r < 0.50f) Buy(p, "item_kevlar");    // 50%
                            else if (r < 0.65f) { Swap(p, "weapon_usp_silencer", "weapon_elite"); Swap(p, "weapon_hkp2000", "weapon_elite"); } // 15%
                            else if (r < 0.75f) { Swap(p, "weapon_usp_silencer", "weapon_p250"); Swap(p, "weapon_hkp2000", "weapon_p250"); }   // 10%
                            else if (r < 0.83f) { Swap(p, "weapon_usp_silencer", "weapon_deagle"); Swap(p, "weapon_hkp2000", "weapon_deagle"); } // 8%
                            else if (r < 0.91f) { Swap(p, "weapon_usp_silencer", "weapon_cz75a"); Swap(p, "weapon_hkp2000", "weapon_cz75a"); }   // 8%
                            else if (r < 0.98f) { Swap(p, "weapon_usp_silencer", "weapon_fiveseven"); Swap(p, "weapon_hkp2000", "weapon_fiveseven"); } //7%
                            else if (r < 1.00f) { Swap(p, "weapon_usp_silencer", "weapon_revolver"); Swap(p, "weapon_hkp2000", "weapon_revolver"); } //2%
                        }
                        else
                        {
                            if (r < 0.50f) Buy(p, "item_kevlar");    // 50%
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
                            if (r < 0.20f) { Swap(p, "weapon_usp_silencer", "weapon_elite"); Swap(p, "weapon_hkp2000", "weapon_elite"); } //20%
                            else if (r < 0.50f) { Swap(p, "weapon_usp_silencer", "weapon_deagle"); Swap(p, "weapon_hkp2000", "weapon_deagle"); } //30%
                            else if (r < 0.65f) { Swap(p, "weapon_usp_silencer", "weapon_cz75a"); Swap(p, "weapon_hkp2000", "weapon_cz75a"); } //15%
                            else if (r < 0.95f) { Swap(p, "weapon_usp_silencer", "weapon_fiveseven"); Swap(p, "weapon_hkp2000", "weapon_fiveseven"); } //30%
                            else if (r < 1.00f) { Swap(p, "weapon_usp_silencer", "weapon_revolver"); Swap(p, "weapon_hkp2000", "weapon_revolver"); } //5%
                        }
                        else
                        {
                            if (r < 0.20f) Swap(p, "weapon_glock", "weapon_elite"); //20%
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
                            if (r < 0.35f) Buy(p, "weapon_m4a1");
                            else if (r < 0.70f) Buy(p, "weapon_m4a1_silencer");
                            else if (r < 0.90f) Buy(p, "weapon_awp");
                            else if (r < 1.00f) Buy(p, "weapon_scar20");
                        }
                        else
                        {
                            if (r < 0.70f) Buy(p, "weapon_ak47");
                            else if (r < 0.90f) Buy(p, "weapon_awp");
                            else if (r < 1.00f) Buy(p, "weapon_g3sg1");
                        }
                    }
                }
            }
        });
        // Drop Weapons
        AddTimer(3.0f, () =>
        {
            if (!IsFirstRoundOfHalf())
            {
                foreach (var team in new[] { CsTeam.CounterTerrorist, CsTeam.Terrorist })
                {
                    if (!_poorPlayersByTeam.TryGetValue(team, out var poor))
                        poor = new List<CCSPlayerController>();
                    poor = poor.Where(p => p.IsValid && p.InGameMoneyServices != null).ToList();

                    var richBots = allPlayers.Where(p => p.IsValid && p.Team == team && p.IsBot && p.InGameMoneyServices?.Account >= 2900).ToList();

                    if (poor.Count == 0 || richBots.Count == 0) continue;

                    var giftedPoor = new HashSet<CCSPlayerController>();

                    var shuffledPoor = poor.OrderBy(_ => Random.Shared.Next()).ToList();
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

                            Server.PrintToChatAll($"{ChatColors.Green}{rich.PlayerName}{ChatColors.Yellow}: {poorPlayer.PlayerName}, I dropped a weapon for ya");
                            given++;
                        }
                    }
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

//----------------------------------------------------------------------------------------------

    private static CCSWeaponBaseVData FindWeaponVDataByName(string weaponName)
    {
        return _findWeaponVDataByName.Invoke(1, weaponName);
    }

    private static bool CanHandleWeaponForTeam(CCSPlayerController player, string itemName)
    {
        bool isCT = player.Team == CsTeam.CounterTerrorist;
        bool isT = player.Team == CsTeam.Terrorist;

        return itemName switch
        {
            "weapon_glock" => isT,
            "weapon_hkp2000" => isCT,
            "weapon_usp_silencer" => isCT,
            "weapon_tec9" => isT,
            "weapon_fiveseven" => isCT,
            "weapon_mac10" => isT,
            "weapon_mp9" => isCT,
            "weapon_sawedoff" => isT,
            "weapon_mag7" => isCT,
            "weapon_galilar" => isT,
            "weapon_ak47" => isT,
            "weapon_sg556" => isT,
            "weapon_famas" => isCT,
            "weapon_m4a1" => isCT,
            "weapon_m4a1_silencer" => isCT,
            "weapon_aug" => isCT,
            "weapon_scar20" => isCT,
            "weapon_g3sg1" => isT,
            "item_defuser" => isCT,

            _ => true
        };
    }

    private static bool TryGetWeaponPrice(string itemName, out int price)
    {
        switch (itemName) // IDK why FindWeaponVDataByName can't handle item prices
        {
            case "item_kevlar": price = 650; return true;
            case "item_assaultsuit": price = 1000; return true;
            case "item_defuser": price = 400; return true;
        }

        price = 0;

        CCSWeaponBaseVData weaponVData = FindWeaponVDataByName(itemName);
        if (weaponVData == null)
            return false;

        price = weaponVData.Price;
        return price >= 0;
    }

    private bool Buy(CCSPlayerController player, string itemName)
    {
        if (!player.IsValid || !player.IsBot || player.InGameMoneyServices == null)
            return false;

        var pawn = player.PlayerPawn.Value;
        if (pawn == null || !pawn.IsValid)
            return false;

        int money = player.InGameMoneyServices.Account;
        int price;

        if (itemName == "item_assaultsuit")
        {
            price = pawn.ArmorValue > 99 ? 350 : 1000;
        }
        else
        {
            if (!CanHandleWeaponForTeam(player, itemName))
                return false;

            if (!TryGetWeaponPrice(itemName, out price))
                return false;
        }

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
        int price;

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

        if (!CanHandleWeaponForTeam(player, itemName))
            return false;

        if (!TryGetWeaponPrice(itemName, out price))
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
