using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Entities.Constants;
using CounterStrikeSharp.API.Modules.Utils;

namespace MatchZy
{
    public partial class MatchZy
    {
        private const double KnifeDropCooldownSeconds = 3.0;

        private readonly Dictionary<ulong, DateTime> knifeDropCooldowns = new();

        [ConsoleCommand("css_drop", "Drops your owned knife for a living teammate to pick up")]
        [ConsoleCommand("css_d", "Drops your owned knife for a living teammate to pick up")]
        public void OnDropKnifeCommand(CCSPlayerController? player, CommandInfo? command)
        {
            if (!IsEligibleKnifeDropPlayer(player))
            {
                if (player != null)
                {
                    PrintToPlayerChat(player, Localizer["matchzy.drop.invalidplayer"]);
                }
                return;
            }

            if (!IsKnifeDropWindowOpen())
            {
                PrintToPlayerChat(player!, Localizer["matchzy.drop.roundstarted"]);
                return;
            }

            DateTime now = DateTime.UtcNow;
            if (knifeDropCooldowns.TryGetValue(player!.SteamID, out DateTime cooldownStartedAt))
            {
                double remainingSeconds = KnifeDropCooldownSeconds - (now - cooldownStartedAt).TotalSeconds;
                if (remainingSeconds > 0)
                {
                    PrintToPlayerChat(player, Localizer["matchzy.drop.cooldown", Math.Ceiling(remainingSeconds)]);
                    return;
                }
            }

            CCSPlayerPawn? pawn = player.PlayerPawn.Value;
            CPlayer_WeaponServices? weaponServices = pawn?.WeaponServices;
            var sourceKnifeHandle = weaponServices?.MyWeapons.FirstOrDefault(handle =>
                handle.IsValid &&
                handle.Value != null &&
                handle.Value.IsValid &&
                IsKnifeEntity(handle.Value));
            if (sourceKnifeHandle == null || !sourceKnifeHandle.IsValid || sourceKnifeHandle.Value == null)
            {
                PrintToPlayerChat(player, Localizer["matchzy.drop.noknife"]);
                return;
            }

            bool hasLivingTeammate = Utilities.GetPlayers()
                .Any(target =>
                    target.Slot != player.Slot &&
                    IsEligibleKnifeDropTarget(target) &&
                    target.TeamNum == player.TeamNum);
            if (!hasLivingTeammate)
            {
                PrintToPlayerChat(player, Localizer["matchzy.drop.noteammates"]);
                return;
            }

            if (!HasAnotherCoreWeapon(player))
            {
                PrintToPlayerChat(player, Localizer["matchzy.drop.requiresweapon"]);
                return;
            }

            try
            {
                // Drop the exact weapon entity from the owner's inventory. Its knife
                // type and finish travel with that entity; no econ fields are copied
                // or fabricated, keeping CounterStrikeSharp guideline protection on.
                Server.ExecuteCommand("mp_drop_knife_enable 1");
                weaponServices!.ActiveWeapon.Raw = sourceKnifeHandle.Raw;
                player.DropActiveWeapon();

                knifeDropCooldowns[player.SteamID] = now;
                PrintToPlayerChat(player, Localizer["matchzy.drop.success"]);
                Log($"[.drop] {player.PlayerName} dropped their owned knife for a teammate");
            }
            catch (Exception exception)
            {
                PrintToPlayerChat(player, Localizer["matchzy.drop.failed"]);
                Log($"[.drop ERROR] Failed to drop {player.PlayerName}'s owned knife: {exception.GetType().Name}: {exception.Message}");
            }
        }

        private bool IsKnifeDropWindowOpen()
        {
            if (isWarmup || isPractice || !matchStarted)
            {
                return true;
            }

            try
            {
                return GetGameRules().FreezePeriod;
            }
            catch (Exception exception)
            {
                Log($"[.drop ERROR] Could not read freeze-period state: {exception.GetType().Name}: {exception.Message}");
                return false;
            }
        }

        private HookResult OnDropWeaponCommand(CCSPlayerController? player, CommandInfo command)
        {
            if (!IsEligibleKnifeDropPlayer(player))
            {
                return HookResult.Continue;
            }

            CCSPlayerPawn? pawn = player!.PlayerPawn.Value;
            CBasePlayerWeapon? activeWeapon = pawn?.WeaponServices?.ActiveWeapon.Value;
            if (activeWeapon == null || !activeWeapon.IsValid || !IsCoreWeaponSlot(activeWeapon))
            {
                return HookResult.Continue;
            }

            if (HasAnotherCoreWeapon(player))
            {
                return HookResult.Continue;
            }

            PrintToPlayerChat(player, Localizer["matchzy.drop.requiresweapon"]);
            return HookResult.Stop;
        }

        private bool IsEligibleKnifeDropPlayer(CCSPlayerController? player)
        {
            return IsPlayerValid(player) &&
                player!.Connected == PlayerConnectedState.PlayerConnected &&
                !player.IsBot &&
                !player.IsHLTV &&
                player.PawnIsAlive &&
                player.Team is CsTeam.Terrorist or CsTeam.CounterTerrorist;
        }

        private bool IsEligibleKnifeDropTarget(CCSPlayerController? player)
        {
            return IsPlayerValid(player) &&
                player!.Connected == PlayerConnectedState.PlayerConnected &&
                !player.IsHLTV &&
                (!player.IsBot || localFillBotsOnFirstConnect) &&
                player.PawnIsAlive &&
                player.Team is CsTeam.Terrorist or CsTeam.CounterTerrorist;
        }

        private static bool IsKnifeEntity(CBasePlayerWeapon weapon)
        {
            return weapon.DesignerName.Contains("knife", StringComparison.OrdinalIgnoreCase) ||
                weapon.DesignerName.Contains("bayonet", StringComparison.OrdinalIgnoreCase);
        }

        private static bool HasAnotherCoreWeapon(CCSPlayerController player)
        {
            var weapons = player.PlayerPawn.Value?.WeaponServices?.MyWeapons;
            if (weapons == null)
            {
                return false;
            }

            int coreWeaponCount = 0;
            foreach (var weaponHandle in weapons)
            {
                if (!weaponHandle.IsValid || weaponHandle.Value == null || !weaponHandle.Value.IsValid)
                {
                    continue;
                }

                if (IsCoreWeaponSlot(weaponHandle.Value) && ++coreWeaponCount > 1)
                {
                    return true;
                }
            }

            return false;
        }

        private static bool IsCoreWeaponSlot(CBasePlayerWeapon weapon)
        {
            if (IsKnifeEntity(weapon))
            {
                return true;
            }

            try
            {
                CCSWeaponBaseVData? weaponData = weapon.As<CCSWeaponBase>().VData;
                return weaponData?.GearSlot is gear_slot_t.GEAR_SLOT_RIFLE or gear_slot_t.GEAR_SLOT_PISTOL;
            }
            catch
            {
                // Grenades, C4 and other equipment are outside the three core
                // weapon slots and may not expose CCSWeaponBase VData.
                return false;
            }
        }

    }
}
