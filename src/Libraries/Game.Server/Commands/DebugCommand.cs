using QuantumCore.API.Game;
using QuantumCore.API.Game.Types.Entities;
using QuantumCore.Game.World.Entities;

namespace QuantumCore.Game.Commands;

[Command("debug_damage", "Print debug information regarding damage calculation, and toggle the per-hit chat breakdown")]
public class DebugCommandDamage : ICommandHandler
{
    public Task ExecuteAsync(CommandContext context)
    {
        var minWeapon = context.Player.GetPoint(EPoint.MIN_WEAPON_DAMAGE);
        var maxWeapon = context.Player.GetPoint(EPoint.MAX_WEAPON_DAMAGE);
        var minAttack = context.Player.GetPoint(EPoint.MIN_ATTACK_DAMAGE);
        var maxAttack = context.Player.GetPoint(EPoint.MAX_ATTACK_DAMAGE);
        context.Player.SendChatMessage($"Weapon Damage: {minWeapon}-{maxWeapon}");
        context.Player.SendChatMessage($"Attack Damage: {minAttack}-{maxAttack}");

        // Also toggles the per-hit "Base Attack value"/"Melee damage"/etc. chat breakdown that
        // Entity.Damage's internal SendDebugDamage sends for every attack this player is involved in -
        // off by default (see PlayerEntity.DebugDamageEnabled's own doc comment for why).
        if (context.Player is PlayerEntity player)
        {
            player.DebugDamageEnabled = !player.DebugDamageEnabled;
            context.Player.SendChatInfo(
                $"Per-hit damage breakdown {(player.DebugDamageEnabled ? "enabled" : "disabled")}.");
        }

        return Task.CompletedTask;
    }
}
