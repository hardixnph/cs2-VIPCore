using System.Text.Json;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Capabilities;
using VipCoreApi;
using VipCoreApi.Enums;

namespace VIP_FastReload;

public class VIPFastReload : BasePlugin
{
    public override string ModuleAuthor => "T3Marius";
    public override string ModuleName => "[VIP] FastReload";
    public override string ModuleVersion => "v2.1.0";

    private IVipCoreApi? _api;
    private FastReload? _fastReloadFeature;

    public override void OnAllPluginsLoaded(bool hotReload)
    {
        _api = IVipCoreApi.Capability.Get();
        if (_api == null) return;

        _fastReloadFeature = new FastReload(this, _api);
    }

    public override void Unload(bool hotReload)
    {
        _fastReloadFeature?.Dispose();
    }

    public class FastReload : VipFeature<JsonElement>
    {
        // the engine's own reload keeps running after the early finish and moves ammo once more when its animation
        // ends: watch the gun until then and undo that
        private const float GuardSeconds = 6f;
        // a reload whose length the engine does not tell (next attack tick not ahead) counts as this long
        private const float FallbackReloadSeconds = 2.5f;
        // 100 % maps to this many times faster
        private const float MaxSpeed = 20f;

        private readonly VIPFastReload _plugin;
        private readonly Dictionary<CCSPlayerController, Reload> _reloads = [];
        private readonly Dictionary<CCSPlayerController, Guard> _guards = [];

        private sealed class Reload(CCSWeaponBase weapon, float speed, float startedAt, int startTick, float originalRate)
        {
            public CCSWeaponBase Weapon { get; } = weapon;
            public float Speed { get; } = speed;
            public float StartedAt { get; } = startedAt;
            public int StartTick { get; } = startTick;
            public float OriginalRate { get; } = originalRate;
            public float DoneAt { get; set; } = float.NaN;
        }

        private sealed class Guard(CCSWeaponBase weapon, float until)
        {
            public CCSWeaponBase Weapon { get; } = weapon;
            public float Until { get; } = until;
            public int Clip { get; set; } = weapon.Clip1;
            public int Reserve { get; set; } = weapon.ReserveAmmo[0];
        }

        public FastReload(VIPFastReload vipFastReload, IVipCoreApi api) : base("FastReload", api)
        {
            _plugin = vipFastReload;
            vipFastReload.RegisterEventHandler<EventWeaponReload>(OnWeaponReload);
            vipFastReload.RegisterListener<Listeners.OnTick>(OnTick);
        }

        public override void OnPlayerDisconnect(CCSPlayerController player, bool vip)
        {
            if (_reloads.Remove(player, out Reload? reload))
                SetRate(reload.Weapon, reload.OriginalRate);
            _guards.Remove(player);
        }

        public override void Dispose()
        {
            _plugin.RemoveListener<Listeners.OnTick>(OnTick);
            foreach (Reload reload in _reloads.Values)
                SetRate(reload.Weapon, reload.OriginalRate);
            _reloads.Clear();
            _guards.Clear();
            base.Dispose();
        }

        // vip.json value is the percent the reload time is cut by: 0 = normal, 50 = 2x faster, 100 = instant;
        // `true` keeps the old instant behaviour
        private float GetReloadSpeed(CCSPlayerController player)
        {
            JsonElement value = GetValue(player);
            float percent = value.ValueKind switch
            {
                JsonValueKind.Number => Math.Clamp(value.GetSingle(), 0f, 100f),
                JsonValueKind.True => 100f,
                _ => 0f
            };
            return percent >= 100f ? MaxSpeed : Math.Min(100f / (100f - percent), MaxSpeed);
        }

        private HookResult OnWeaponReload(EventWeaponReload @event, GameEventInfo info)
        {
            CCSPlayerController? player = @event.Userid;
            if (player is not { IsValid: true } || !IsFeatureEnabled(player)) return HookResult.Continue;

            if (player.PlayerPawn.Value?.WeaponServices?.ActiveWeapon.Value is not { IsValid: true } activeWeapon)
                return HookResult.Continue;

            float speed = GetReloadSpeed(player);
            if (speed <= 1f) return HookResult.Continue;

            // a new reload legitimately takes reserve ammo, so the previous reload's guard must not undo it
            _guards.Remove(player);

            // the reload animation plays faster on the client; the ammo follows in OnTick when it ends
            CCSWeaponBase weapon = activeWeapon.As<CCSWeaponBase>();
            if (!_reloads.ContainsKey(player))
                _reloads[player] = new Reload(weapon, speed, Server.CurrentTime, Server.TickCount, weapon.WeaponActionPlaybackRate);

            SetRate(weapon, speed);
            return HookResult.Continue;
        }

        private void OnTick()
        {
            if (_reloads.Count == 0 && _guards.Count == 0) return;

            float now = Server.CurrentTime;
            foreach ((CCSPlayerController player, Reload reload) in _reloads)
            {
                CCSWeaponBase weapon = reload.Weapon;

                // cancelled (weapon switch)
                if (!weapon.IsValid || !player.IsValid
                    || player.PlayerPawn.Value?.WeaponServices?.ActiveWeapon.Value?.Handle != weapon.Handle)
                {
                    _reloads.Remove(player);
                    SetRate(weapon, reload.OriginalRate);
                    continue;
                }

                // the engine loaded the gun itself when the fast animation ended, but its attack block still waits
                // for the normal reload time
                if (!weapon.InReload)
                {
                    _reloads.Remove(player);
                    SetRate(weapon, reload.OriginalRate);
                    AllowAttack(weapon);
                    _guards[player] = new Guard(weapon, now + GuardSeconds);
                    continue;
                }

                if (float.IsNaN(reload.DoneAt))
                {
                    // at its start the engine blocks attacks until its own (normal speed) reload ends
                    int ticks = weapon.NextPrimaryAttackTick - reload.StartTick;
                    float normal = ticks > 0 ? ticks * Server.TickInterval : FallbackReloadSeconds;
                    reload.DoneAt = reload.StartedAt + normal / reload.Speed;
                }

                if (now < reload.DoneAt) continue;

                _reloads.Remove(player);
                SetRate(weapon, reload.OriginalRate);
                if (Finish(weapon))
                    _guards[player] = new Guard(weapon, now + GuardSeconds);
            }

            foreach ((CCSPlayerController player, Guard guard) in _guards)
            {
                if (now > guard.Until || !guard.Weapon.IsValid)
                {
                    _guards.Remove(player);
                    continue;
                }

                Watch(guard);
            }
        }

        // only the engine's late reload lowers the reserve while guarded (shots use the clip, pickups raise it):
        // it is put back together with the clip it refilled, so the reload costs one magazine
        private static void Watch(Guard guard)
        {
            CCSWeaponBase weapon = guard.Weapon;
            int reserve = weapon.ReserveAmmo[0];
            if (reserve >= guard.Reserve)
            {
                guard.Reserve = reserve;
                guard.Clip = weapon.Clip1;
                return;
            }

            weapon.Clip1 = guard.Clip;
            weapon.ReserveAmmo[0] = guard.Reserve;
            weapon.InReload = false;
            Utilities.SetStateChanged(weapon, "CBasePlayerWeapon", "m_iClip1");
            Utilities.SetStateChanged(weapon, "CBasePlayerWeapon", "m_pReserveAmmo");
            Utilities.SetStateChanged(weapon, "CCSWeaponBase", "m_bInReload");
        }

        // does what the engine's reload does at its end; guns whose reserve counts magazines take one full magazine
        private static bool Finish(CCSWeaponBase weapon)
        {
            CBasePlayerWeaponVData? data = weapon.VData;
            int max = data?.MaxClip1 ?? 0;
            int reserve = weapon.ReserveAmmo[0];
            bool asClips = data?.ReserveAmmoAsClips == true;
            if (max <= 0 || reserve <= 0 || weapon.Clip1 >= max) return false;

            int rounds = asClips ? max - weapon.Clip1 : Math.Min(max - weapon.Clip1, reserve);
            weapon.Clip1 += rounds;
            weapon.ReserveAmmo[0] = reserve - (asClips ? 1 : rounds);
            weapon.InReload = false;
            weapon.NextPrimaryAttackTick = Server.TickCount;
            Utilities.SetStateChanged(weapon, "CBasePlayerWeapon", "m_iClip1");
            Utilities.SetStateChanged(weapon, "CBasePlayerWeapon", "m_pReserveAmmo");
            Utilities.SetStateChanged(weapon, "CCSWeaponBase", "m_bInReload");
            Utilities.SetStateChanged(weapon, "CBasePlayerWeapon", "m_nNextPrimaryAttackTick");
            return true;
        }

        private static void AllowAttack(CCSWeaponBase weapon)
        {
            if (weapon.NextPrimaryAttackTick <= Server.TickCount) return;

            weapon.NextPrimaryAttackTick = Server.TickCount;
            Utilities.SetStateChanged(weapon, "CBasePlayerWeapon", "m_nNextPrimaryAttackTick");
        }

        private static void SetRate(CCSWeaponBase weapon, float rate)
        {
            if (!weapon.IsValid || weapon.WeaponActionPlaybackRate == rate) return;

            weapon.WeaponActionPlaybackRate = rate;
            Utilities.SetStateChanged(weapon, "CCSWeaponBase", "m_flWeaponActionPlaybackRate");
        }
    }
}
