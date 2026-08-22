using CommandLine;
using QuantumCore.API.Game;

namespace QuantumCore.Game.Commands;

/// <summary>
/// Mounts the caller on the horse matching their current HorseLevel (see HorseVnums.cs), gated on the
/// real per-tier character-level requirement (HorseStats.MinLevel - 25/35/50, from c_aHorseStat's
/// iMinLevel), or on an explicit mob proto id if one is given (bypasses BOTH the HorseLevel gate and the
/// character-level gate - useful for GM testing).
/// </summary>
[Command("mount", "Mounts you on your horse (or an explicit mob proto id)")]
public class MountCommand : ICommandHandler<MountCommandOptions>
{
    public Task ExecuteAsync(CommandContext<MountCommandOptions> context)
    {
        if (context.Arguments.Vnum is { } explicitVnum)
        {
            context.Player.Mount(explicitVnum);
            context.Player.SendChatInfo($"Mounted on proto {explicitVnum}");
            return Task.CompletedTask;
        }

        // No-arg form toggles: already mounted -> dismount, matching /dismount exactly (used by the
        // Ctrl+G client hotkey, which always calls plain "/mount" and relies on this to act as a toggle).
        if (context.Player.MountVnum != 0)
        {
            context.Player.Unmount();
            context.Player.SendChatInfo("Dismounted");
            return Task.CompletedTask;
        }

        var horseLevel = context.Player.Player.HorseLevel;
        var vnum = HorseVnums.ForLevel(horseLevel);
        if (vnum == 0)
        {
            context.Player.SendChatInfo("You don't have a horse yet - see /sethorselevel");
            return Task.CompletedTask;
        }

        // Real per-tier character-level gate (c_aHorseStat's iMinLevel: 25/35/50) - separate from the
        // level-10 gate HorseTrialQuest already enforces just to access the mount menu at all. An explicit
        // `/mount <vnum>` still bypasses this, same as it already bypasses the HorseLevel gate above - see
        // HorseStats.cs's doc comment.
        var requiredLevel = HorseStats.MinLevel(horseLevel);
        if (context.Player.Player.Level < requiredLevel)
        {
            context.Player.SendChatInfo(
                $"You need to be level {requiredLevel} to ride this horse (you're level {context.Player.Player.Level})");
            return Task.CompletedTask;
        }

        context.Player.Mount(vnum);
        context.Player.SendChatInfo($"Mounted on your level {horseLevel} horse (proto {vnum})");
        return Task.CompletedTask;
    }
}

public class MountCommandOptions
{
    [Value(0)] public uint? Vnum { get; set; }
}
