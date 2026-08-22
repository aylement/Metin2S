using System.Numerics;
using CommandLine;
using Microsoft.Extensions.Logging;
using QuantumCore.API;
using QuantumCore.API.Game;
using QuantumCore.API.Game.Types;
using QuantumCore.API.Game.World;
using QuantumCore.Game.World;

namespace QuantumCore.Game.Commands;

[Command("goto", "Warp to a position")]
public class GotoCommand : ICommandHandler<GotoCommandOptions>
{
    private readonly IWorld _world;
    private readonly ILogger<GotoCommand> _logger;

    public GotoCommand(IWorld world, ILogger<GotoCommand> logger)
    {
        _world = world;
        _logger = logger;
    }

    public Task ExecuteAsync(CommandContext<GotoCommandOptions> context)
    {
        if (!string.IsNullOrWhiteSpace(context.Arguments.Map))
        {
            var maps = _world.FindMapsByName(context.Arguments.Map);
            if (maps.Length > 1)
            {
                context.Player.SendChatInfo("Map name is ambiguous:");
                foreach (var map in maps)
                {
                    context.Player.SendChatInfo($"- {map.Name}");
                }

                return Task.CompletedTask;
            }

            if (maps.Length == 0)
            {
                context.Player.SendChatInfo("Unknown map");
                return Task.CompletedTask;
            }

            var targetMap = maps[0];

            // Prefer a real spawn point (Town.txt, parsed into Map.TownCoordinates - already absolute
            // world coordinates, see Map's constructor) over the map's raw geometric bounding-box center:
            // the center is an arbitrary point with no guarantee it's walkable terrain at all - confirmed
            // live landing a player stuck on unwalkable rock on metin2_map_n_flame_01, whose real usable
            // area sits far from its bounding-box middle. Falls back to the old center behavior only for
            // a map with no Town.txt (e.g. a dungeon/instance with no fixed spawn).
            var townCoordinates = targetMap.TownCoordinates;
            Coordinates destination;
            if (townCoordinates is not null)
            {
                destination = context.Player.Player.Empire switch
                {
                    EEmpire.JINNO => townCoordinates.Jinno,
                    EEmpire.SHINSOO => townCoordinates.Shinsoo,
                    EEmpire.CHUNJO => townCoordinates.Chunjo,
                    _ => townCoordinates.Common
                };
            }
            else
            {
                destination = new Coordinates(
                    targetMap.Position.X + targetMap.Width * Map.MAP_UNIT / 2,
                    targetMap.Position.Y + targetMap.Height * Map.MAP_UNIT / 2);
            }

            // A same-map goto (the `else` branch below) is safe as-is: PlayerEntity.Move() itself refuses
            // a BLOCK/OBJECT destination without moving. A cross-map goto is NOT: it always goes through
            // PlayerEntity.Warp(), which teleports unconditionally with no attribute check at all (it
            // disconnects/reconnects the client at the new position instead of walking there) - so an
            // unwalkable destination (the Town.txt spawn point itself, or the bounding-box center fallback
            // above) leaves the player stuck with no way to move off it. Confirmed live: n_flame_01's own
            // real Town.txt Jinno spawn point sits on unwalkable rock. Nudge to the nearest walkable cell
            // before warping, same map only.
            if (targetMap is Map targetMapImpl)
            {
                destination = FindWalkableNearby(targetMapImpl, destination);
            }

            context.Player.Move((int)destination.X, (int)destination.Y);
        }
        else
        {
            if (context.Player.Map is null)
            {
                _logger.LogCritical("Player's map is null, this should never happen");
                context.Player.Connection.Close();
                return Task.CompletedTask;
            }

            if (context.Arguments.X < 0 || context.Arguments.Y < 0)
                context.Player.SendChatInfo("The X and Y position must be positive");
            else
            {
                var x = (int)context.Player.Map.Position.X + (context.Arguments.X * 100);
                var y = (int)context.Player.Map.Position.Y + (context.Arguments.Y * 100);
                context.Player.Move(x, y);
            }
        }

        context.Player.ShowEntity(context.Player.Connection);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Searches an expanding square ring around <paramref name="destination"/> (step 100 units = 1m, same
    /// raw-unit convention as <see cref="Map.SPAWN_POSITION_MULTIPLIER"/>) for the nearest cell with
    /// neither <see cref="EMapAttributes.BLOCK"/> nor <see cref="EMapAttributes.OBJECT"/> set, stopping at
    /// <see cref="MaxSearchRadius"/> (30m - generous enough to clear a single rock/building without ending
    /// up somewhere unrelated) and falling back to the original <paramref name="destination"/> unchanged
    /// if nothing walkable turns up in range.
    /// </summary>
    private static Coordinates FindWalkableNearby(Map map, Coordinates destination)
    {
        const int Step = 100;
        const int MaxSearchRadius = 3000;

        if (IsWalkable(map, destination)) return destination;

        for (var radius = Step; radius <= MaxSearchRadius; radius += Step)
        {
            for (var dx = -radius; dx <= radius; dx += Step)
            {
                // Only test the outer edge of this ring - smaller |dx|/|dy| combinations were already
                // tested (and rejected) at a smaller radius.
                var dys = Math.Abs(dx) == radius
                    ? Enumerable.Range(-radius / Step, radius * 2 / Step + 1).Select(i => i * Step)
                    : [-radius, radius];

                foreach (var dy in dys)
                {
                    Coordinates candidate;
                    try
                    {
                        candidate = destination + new Vector2(dx, dy);
                    }
                    catch (OverflowException)
                    {
                        continue; // ring stepped past (0;0) - not a real coordinate, skip it
                    }

                    if (map.IsPositionInside((int)candidate.X, (int)candidate.Y) && IsWalkable(map, candidate))
                    {
                        return candidate;
                    }
                }
            }
        }

        return destination;
    }

    private static bool IsWalkable(Map map, Coordinates coords) =>
        !map.IsAttr(coords, EMapAttributes.BLOCK | EMapAttributes.OBJECT);
}

public class GotoCommandOptions
{
    [Option('m', "map")] public string? Map { get; set; }

    [Value(0)] public int X { get; set; }

    [Value(1)] public int Y { get; set; }
}