using QuantumCore.API;
using QuantumCore.API.Core.Models;
using QuantumCore.API.Game.World;

namespace QuantumCore.Game.Quest;

/// <summary>
/// The horse keeper NPC's trial: kill enough Savage Archers within a time limit to earn a horse. All
/// numbers below are taken directly from the original game's own horse quest scripts (see
/// [[client-server-original-source]] in persistent memory for exactly where), not invented, with one
/// exception noted below:
/// - NPC vnum 20349 and item 50050 "Horse Medal" (마패): confirmed real, used by horse_ride.quest,
///   horse_exchange_ticket.quest and horse_upgrade.quest alike.
/// - Mob 503 "Savage Archer": confirmed real (level 35 monster in this repo's own mob_proto).
/// - 30-minute time limit and 100-kill requirement: taken from horse_upgrade.quest's own kill-test state
///   (`pc.setqf("limit_time", get_time()+30*60)`, `kill_count>=100`) - that quest actually splits the 100
///   kills across two different archer mobs (2105+2107) for the grade 1->2 upgrade test specifically, not
///   the very first horse unlock; reused here as the closest real reference for "kill X within 30 minutes"
///   available, applied to the single mob (503) already confirmed correct for this quest.
/// - Level 10 requirement: taken from horse_ride.quest's own gate for interacting with the mount menu at
///   all (`pc.level>=10`).
///
/// Deliberately NOT persisted (in-memory only - see PlayerData.HorseLevel's own doc comment for the
/// project's general stance on this): _killCount/_deadline reset if the player disconnects mid-trial.
/// The END RESULT (HorseLevel=1 on success) DOES persist normally though, since it flows through the
/// already-persisted PlayerData.HorseLevel on next logout, same as /sethorselevel.
///
/// Note this quest's own level-10 gate (above) is deliberately lower than HorseStats.MinLevel(1) = 25 -
/// they're two different real gates from two different source scripts (this one just for accessing the
/// mount menu/trial at all, the other per-tier for actually riding). A player can finish this trial at
/// level 10-24 and "own" HorseLevel 1, but /mount will still refuse until they reach level 25 - working as
/// intended, not a bug to reconcile.
/// </summary>
[Quest]
public class HorseTrialQuest : Quest
{
    private const uint NpcVnum = 20349; // Horse Keeper
    private const uint MedalItemId = 50050; // "Horse Medal" (마패)
    private const uint TargetMobId = 503; // "Savage Archer"
    private const int RequiredKills = 100;
    private const byte RequiredCharacterLevel = 10;
    private static readonly TimeSpan TimeLimit = TimeSpan.FromMinutes(30);

    private int _killCount;
    private DateTime? _deadline;

    public HorseTrialQuest(QuestState state, IPlayerEntity player) : base(state, player)
    {
    }

    public override void Init()
    {
        GameEventManager.RegisterNpcClickEvent("Horse Trial", NpcVnum, Player.Vid, OnTalkAsync,
            p => p.Vid == Player.Vid);
        GameEventManager.RegisterMonsterKillEvent("Horse Trial", TargetMobId, Player.Vid, OnKillAsync,
            p => p.Vid == Player.Vid);
    }

    private async Task OnTalkAsync(IPlayerEntity player)
    {
        if (Player.Player.Level < RequiredCharacterLevel)
        {
            Text($"Come back once you've reached level {RequiredCharacterLevel}, {Player.Name}.");
            Done();
            return;
        }

        if (Player.Player.HorseLevel > 0)
        {
            Text("You already have a horse - no need for another trial.");
            Done();
            return;
        }

        if (_deadline is { } deadline)
        {
            if (DateTime.UtcNow < deadline)
            {
                var remaining = deadline - DateTime.UtcNow;
                Text($"Still going! {_killCount}/{RequiredKills} Savage Archers down, " +
                     $"{(int)remaining.TotalMinutes} minute(s) left.");
                Done();
                return;
            }

            Text("Time's up - you didn't make it in time. Bring another Horse Medal to try again.");
            _deadline = null;
            _killCount = 0;
            Done();
            return;
        }

        var medal = FindMedal();
        if (medal is null)
        {
            Text($"Bring me a Horse Medal and I'll let you prove yourself.");
            Done();
            return;
        }

        Text($"Kill {RequiredKills} Savage Archers within {TimeLimit.TotalMinutes:0} minutes and " +
             "you'll have earned your horse. Ready?");
        var choice = await ChoiceAsync(false, "Start the trial", "Not now");
        if (choice == 1)
        {
            await Player.DestroyItemAsync(medal);
            _deadline = DateTime.UtcNow + TimeLimit;
            _killCount = 0;
            Text($"The clock is running - {TimeLimit.TotalMinutes:0} minutes, go!");
        }

        Done();
    }

    private Task OnKillAsync(IPlayerEntity player)
    {
        if (_deadline is not { } deadline || DateTime.UtcNow >= deadline)
        {
            return Task.CompletedTask;
        }

        _killCount++;
        if (_killCount >= RequiredKills)
        {
            Player.Player.HorseLevel = 1;
            _deadline = null;
            player.SendChatInfo("You've done it! Return to the Horse Keeper to claim your horse.");
        }

        return Task.CompletedTask;
    }

    private ItemInstance? FindMedal() => Player.Inventory.Items.FirstOrDefault(i => i.ItemId == MedalItemId);
}
