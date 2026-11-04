using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Capabilities;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Utils;
using MenuManager;
using Microsoft.Extensions.Logging;
using System.Text.Json;
using VipCoreApi;
using VipCoreApi.Enums;

namespace VIP_WeaponsMenu;

public class VipWeaponsMenu : BasePlugin
{
    public override string ModuleAuthor => "daffyy";
    public override string ModuleName => "[VIP] WeaponsMenu";
    public override string ModuleVersion => "v2.1.0";

    private WeaponsMenu? _weaponsMenu;
    private IVipCoreApi? _api;

    public override void OnAllPluginsLoaded(bool hotReload)
    {
        _api = IVipCoreApi.Capability.Get();
        if (_api == null) return;

        IMenuApi? menuApi = null;
        try { menuApi = new PluginCapability<IMenuApi?>("menu:nfcore").Get(); } catch { }
        if (menuApi == null)
        {
            Logger.LogError("MenuManagerCS2 (menu:nfcore) is not loaded, the weapons menu is disabled");
            return;
        }

        _weaponsMenu = new WeaponsMenu(this, _api, menuApi);
    }
    
    public override void Unload(bool hotReload)
    {
        _weaponsMenu?.Dispose();
    }
}

public class WeaponsMenu : VipFeature
{
    private VipWeaponsMenu _weaponsMenu;
    private readonly IMenuApi _menuApi;
    private static CCSGameRules? _gameRules;
    private const float MenuTimeSeconds = 15f;

    // the saved package name is stored per team in VIPCore cookies, so it survives reconnects and map changes
    private const string SelectionCookieT = "weaponsmenu_t";
    private const string SelectionCookieCT = "weaponsmenu_ct";

    // bumped on every menu this module opens; the auto-close timer only closes a menu that is still the latest one
    private readonly Dictionary<CCSPlayerController, int> _menuVersion = new();

    private readonly Dictionary<string, int> _grenadeIndex = new()
    {
        ["flashbang"] = 14,
        ["smokegrenade"] = 15,
        ["decoy"] = 17,
        ["incgrenade"] = 16,
        ["molotov"] = 16,
        ["hegrenade"] = 13
    };

    public WeaponsMenu(VipWeaponsMenu weaponsMenu, IVipCoreApi api, IMenuApi menuApi) : base("WeaponsMenu", api, FeatureType.Hide)
    {
        _weaponsMenu = weaponsMenu;
        _menuApi = menuApi;
        _weaponsMenu.RegisterEventHandler<EventPlayerSpawn>(EventPlayerSpawn);
        _weaponsMenu.RegisterEventHandler<EventPlayerDisconnect>(EventPlayerDisconnect);
        _weaponsMenu.RegisterListener<Listeners.OnMapStart>(OnMapStart);
        _weaponsMenu.AddCommand("css_vwmenu", "Reset vip package selection", OnVwMenuCommand);
    }

    private void OnVwMenuCommand(CCSPlayerController? caller, CommandInfo command)
    {
        if (caller == null || !caller.IsValid) return;
        if (!IsFeatureEnabled(caller))
            return;

        RemovePlayerSelection(caller);

        Api.PrintToChat(caller, GetTranslatedText("weaponsmenu.resetinfo.reset"));
    }

    private void OnMapStart(string mapName)
    {
        _weaponsMenu.AddTimer(1.0f, () =>
        {
            _gameRules = Utilities.FindAllEntitiesByDesignerName<CCSGameRulesProxy>("cs_gamerules").First().GameRules;
        });
    }

    private HookResult EventPlayerDisconnect(EventPlayerDisconnect @event, GameEventInfo info)
    {
        var player = @event.Userid;

        if (player == null || !player.IsValid || player.IsBot)
            return HookResult.Continue;

        _menuVersion.Remove(player);

        return HookResult.Continue;
    }

    private HookResult EventPlayerSpawn(EventPlayerSpawn @event, GameEventInfo info)
    {
        if (_gameRules is { WarmupPeriod: true })
            return HookResult.Continue;

        var player = @event.Userid;

        if (player == null || !player.IsValid || player.IsBot || player.TeamNum < 2)
            return HookResult.Continue;

        if (!IsFeatureEnabled(player))
            return HookResult.Continue;

        _menuApi.CloseMenu(player);
        Server.NextFrame(() => _weaponsMenu.AddTimer(1.0f, () => CreateMenu(player)));

        return HookResult.Continue;
    }

    private void CreateMenu(CCSPlayerController player)
    {
        if (!player.PawnIsAlive || player.PlayerPawn.Value == null || player.Connected != PlayerConnectedState.Connected)
            return;

        var json = GetFeatureValue<JsonElement>(player).GetRawText();
        var weaponsSettings = JsonSerializer.Deserialize<WeaponsMenuSettings>(json);

        if (weaponsSettings == null) return;

        var teamPackages = player.Team == CsTeam.Terrorist ? weaponsSettings.T : weaponsSettings.CT;

        if (GetPlayerSelection(player, teamPackages, out var selection) && selection != null)
        {
            if (GetActualRound() < selection.Round)
                return;

            if (_gameRules is { BuyTimeEnded: true } || !player.PlayerPawn.Value.InBuyZone)
                return;

            RemoveWeapons(player);

            selection.Weapons.ForEach(w =>
            {
                if (_grenadeIndex.TryGetValue(w, out var ammoIndex))
                {
                    if (player.PlayerPawn.Value?.WeaponServices?.Ammo[ammoIndex] == 0)
                        player.GiveNamedItem(w);
                    else
                    {
                        if (player.PlayerPawn.Value?.WeaponServices?.MyWeapons
                                .ToList()
                                .Find(m => m.Value != null && m.Value.DesignerName == w) == null)
                            player.GiveNamedItem(w);
                    }
                }
                else
                {
                    player.GiveNamedItem(w);
                }
            });

            return;
        }

        var round = GetActualRound();
        var packages = teamPackages
            .Where(p => round >= p.Value.Round)
            .ToList();

        if (packages.Count == 0) return;

        var menu = _menuApi.GetMenu(GetTranslatedText("weaponsmenu.title"));
        menu.ExitButton = true;

        foreach (var package in packages)
        {
            menu.AddMenuOption(package.Key, (_, _) =>
            {
                if (!player.IsValid || !IsFeatureEnabled(player) || !player.PawnIsAlive || player.PlayerPawn.Value == null)
                    return;
                if (GetActualRound() < package.Value.Round)
                    return;
                if (_gameRules is { BuyTimeEnded: true } || !player.PlayerPawn.Value.InBuyZone)
                    return;

                RemoveWeapons(player);

                package.Value.Weapons.ForEach(w =>
                {
                    if (_grenadeIndex.TryGetValue(w, out var ammoIndex))
                    {
                        if (player.PlayerPawn.Value?.WeaponServices?.Ammo[ammoIndex] == 0)
                            player.GiveNamedItem(w);
                        else
                        {
                            if (player.PlayerPawn.Value?.WeaponServices?.MyWeapons.ToList()
                                    .Find(m => m.Value != null && m.Value.DesignerName == w) == null)
                                player.GiveNamedItem(w);
                        }
                    } 
                    else
                    {
                        player.GiveNamedItem(w);
                    }
                });

                CreateSubMenu(player, package.Key);

            });
        }

        OpenMenu(player, menu);
    }

    private void CreateSubMenu(CCSPlayerController player, string packageName)
    {
        var menu = _menuApi.GetMenu(GetTranslatedText("weaponsmenu.wanttosave"));

        menu.AddMenuOption(GetTranslatedText("weaponsmenu.wanttosave.yes"), (_, _) =>
        {
            AddPlayerSelection(player, packageName);
            Api.PrintToChat(player, GetTranslatedText("weaponsmenu.saved"));
            Api.PrintToChat(player, GetTranslatedText("weaponsmenu.resetinfo.info"));
            CloseMenu(player);
        });
        menu.AddMenuOption(GetTranslatedText("weaponsmenu.wanttosave.no"), (_, _) =>
        {
            Api.PrintToChat(player, GetTranslatedText("weaponsmenu.notsaved"));
            CloseMenu(player);
        });

        OpenMenu(player, menu);
    }

    // MenuManagerCS2 menus have no timeout, so close ours after MenuTimeSeconds unless another of ours replaced it
    private void OpenMenu(CCSPlayerController player, CounterStrikeSharp.API.Modules.Menu.IMenu menu)
    {
        var version = _menuVersion.GetValueOrDefault(player) + 1;
        _menuVersion[player] = version;

        menu.Open(player);

        _weaponsMenu.AddTimer(MenuTimeSeconds, () =>
        {
            if (player.IsValid && _menuVersion.GetValueOrDefault(player) == version)
                CloseMenu(player);
        });
    }

    private void CloseMenu(CCSPlayerController player)
    {
        _menuVersion[player] = _menuVersion.GetValueOrDefault(player) + 1;
        if (_menuApi.HasOpenedMenu(player))
            _menuApi.CloseMenu(player);
    }

    private static int GetActualRound()
    {
        return _gameRules?.RoundsPlayedThisPhase + 1 ?? 1;
    }
    
    private static string? GetSelectionCookie(CCSPlayerController player) => player.Team switch
    {
        CsTeam.Terrorist => SelectionCookieT,
        CsTeam.CounterTerrorist => SelectionCookieCT,
        _ => null
    };

    private bool GetPlayerSelection(CCSPlayerController player, Dictionary<string, WeaponSelection> packages,
        out WeaponSelection? selection)
    {
        selection = null;

        var cookie = GetSelectionCookie(player);
        if (cookie == null)
            return false;

        var packageName = GetPlayerCookie<string>(player, cookie);
        if (string.IsNullOrEmpty(packageName))
            return false;

        // the package may have been removed from vip.json since it was saved
        return packages.TryGetValue(packageName, out selection);
    }

    private void AddPlayerSelection(CCSPlayerController player, string packageName)
    {
        var cookie = GetSelectionCookie(player);
        if (cookie != null)
            SetPlayerCookie(player, cookie, packageName);
    }

    private void RemovePlayerSelection(CCSPlayerController player)
    {
        SetPlayerCookie(player, SelectionCookieT, string.Empty);
        SetPlayerCookie(player, SelectionCookieCT, string.Empty);
    }

    private static void RemoveWeapons(CCSPlayerController player)
    {
        if (player.PlayerPawn.Value?.WeaponServices == null || player.PlayerPawn.Value?.ItemServices == null)
            return;

        var weapons = player.PlayerPawn.Value.WeaponServices.MyWeapons.ToList();

        if (weapons.Count == 0)
            return;

        foreach (var weapon in weapons)
        {
            if (!weapon.IsValid || weapon.Value == null ||
                !weapon.Value.IsValid)
                continue;

            if (weapon.Value.Entity == null) continue;
            
            var weaponData = weapon.Value.As<CCSWeaponBase>().VData;
            if (weaponData == null)
                continue;

            if (weaponData.GearSlot is gear_slot_t.GEAR_SLOT_RIFLE or gear_slot_t.GEAR_SLOT_PISTOL)
            {
                weapon.Value?.AddEntityIOEvent("Kill", weapon.Value, null, "", 0.12f);
            }
        }
    }
}


public class WeaponsMenuSettings
{
    public required Dictionary<string, WeaponSelection> CT { get; init; }
    public required Dictionary<string, WeaponSelection> T { get; init; }
}

public class WeaponSelection(List<string> weapons, int round)
{
    public required List<string> Weapons { get; init; } = weapons;
    public required int Round { get; init; } = round;
}