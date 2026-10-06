using System.Numerics;
using QuantumCore.API.Game;
using QuantumCore.API.Game.Types.Entities;
using QuantumCore.Game.World.Entities;

namespace QuantumCore.Game.Commands;

[Command("pull", "All monsters in range will quickly move towards you")]
[Command("pull_monster", "All monsters in range will quickly move towards you")]
public class PullCommand : ICommandHandler
{
    public Task ExecuteAsync(CommandContext context)
    {
        const int MAX_DISTANCE = 10000;
        const int MIN_DISTANCE = 100;
        var p = context.Player;
        context.Player.ForEachNearbyEntity(e =>
        {
            if (e.Type != EEntityType.MONSTER) return;

            // Stones (Metins) have the NOMOVE flag in the real game and are meant to be completely
            // immovable - live-confirmed this command used to also grab them, and repeatedly calling
            // /pull on the same stone dragged it further and further away each time (its Goto() target
            // was computed wrong too - see below) until it ended up outside view distance and
            // effectively "disappeared".
            if (e is MonsterEntity { IsStone: true }) return;

            var dist = Vector2.Distance(new Vector2(p.PositionX, p.PositionY),
                new Vector2(e.PositionX, e.PositionY));
            if (dist is > MAX_DISTANCE or < MIN_DISTANCE)
                return;

            // Was: computed a direction vector toward the player, then added it (plus the FULL distance
            // value) onto the PLAYER's own position - nonsense math (a monster 5000 units away would get
            // a target 5000+ units past the player, not next to them), acknowledged as broken in a
            // comment ("moves to the wrong side of the player... good enough for now"). Fixed: just walk
            // the monster straight to where the player currently stands - simple and actually correct.
            // Also sets Target so the monster's AI treats this as real aggro (attacks once in range)
            // instead of just relocating there and standing idle.
            e.Target = p;
            e.Goto(p.PositionX, p.PositionY, context.Player.Connection.Server.Clock.Now);
        });
        return Task.CompletedTask;
    }
}
