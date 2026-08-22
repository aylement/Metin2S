using QuantumCore.API.Core.Models;
using QuantumCore.API.Game.World;
using QuantumCore.Game.Quest;

namespace QuantumCore.Game;

public static class GameEventManager
{

    private struct NpcClickEvent
    {
        public string Name { get; set; }
        public uint NpcId { get; set; }

        /// <summary>Null for world-scoped registrations (e.g. shops) that aren't tied to one player and
        /// should never be swept away by <see cref="UnregisterPlayer"/>.</summary>
        public uint? PlayerVid { get; set; }
        public Func<IPlayerEntity, Task> Callback { get; set; }
        public Func<IPlayerEntity, bool>? Condition { get; set; }
    }

    private struct NpcGiveEvent
    {
        public string Name { get; set; }
        public uint NpcId { get; set; }
        public uint? PlayerVid { get; set; }
        public Func<IPlayerEntity, ItemInstance, Task> Callback { get; set; }
        public Func<IPlayerEntity, ItemInstance, bool>? Condition { get; set; }
    }

    private struct MonsterKillEvent
    {
        public string Name { get; set; }
        public uint MobId { get; set; }
        public uint? PlayerVid { get; set; }
        public Func<IPlayerEntity, Task> Callback { get; set; }
        public Func<IPlayerEntity, bool>? Condition { get; set; }
    }

    private static readonly Dictionary<uint, List<NpcClickEvent>> NpcClickEvents = new();
    private static readonly Dictionary<uint, List<NpcGiveEvent>> NpcGiveEvents = new();
    private static readonly Dictionary<uint, List<MonsterKillEvent>> MonsterKillEvents = new();

    public static async Task OnNpcClickAsync(uint npcId, IPlayerEntity player)
    {
        ArgumentNullException.ThrowIfNull(player);
        if (!NpcClickEvents.TryGetValue(npcId, out var value))
        {
            return;
        }

        var events = value.Where(e => e.Condition is null || e.Condition(player)).ToList();
        if (events.Count > 1)
        {
            // todo make sure interface IPlayerEntity is enough
            var internalQuest = player.GetQuestInstance<InternalQuest>();

            if (internalQuest is null)
            {
                return;
            }

            var selected = await internalQuest.SelectQuestAsync(events.Select(e => e.Name));
            if (events[selected].Callback.Target is not Quest.Quest)
            {
                internalQuest.EndQuest();
            }

            await events[selected].Callback(player);

            return;
        }

        if (events.Count == 0)
        {
            return;
        }

        await events[0].Callback(player);
    }

    public static async Task OnNpcGiveAsync(uint npcId, IPlayerEntity player, ItemInstance item)
    {
        ArgumentNullException.ThrowIfNull(player);
        if (!NpcGiveEvents.TryGetValue(npcId, out var value))
        {
            return;
        }

        var events = value.Where(e => e.Condition is null || e.Condition(player, item)).ToList();
        if (events.Count > 1)
        {
            // todo make sure interface IPlayerEntity is enough
            var internalQuest = player.GetQuestInstance<InternalQuest>();

            if (internalQuest is null) return;

            var selected = await internalQuest.SelectQuestAsync(events.Select(e => e.Name));
            if (events[selected].Callback.Target is not Quest.Quest)
            {
                internalQuest.EndQuest();
            }

            await events[selected].Callback(player, item);

            return;
        }

        if (events.Count == 0)
        {
            return;
        }

        await events[0].Callback(player, item);
    }

    /// <summary>
    /// Fired when a player lands the killing blow on a monster (see Entity.Damage()) - the equivalent of
    /// the original game's `&lt;mobvnum&gt;.kill` quest trigger. Unlike NPC click/give events, ALL
    /// matching registrations are invoked (not just one), since multiple quests legitimately tracking the
    /// same kill is normal and there is no dialogue to disambiguate between.
    /// </summary>
    public static async Task OnMonsterKillAsync(uint mobId, IPlayerEntity killer)
    {
        ArgumentNullException.ThrowIfNull(killer);
        if (!MonsterKillEvents.TryGetValue(mobId, out var value))
        {
            return;
        }

        foreach (var e in value.Where(e => e.Condition is null || e.Condition(killer)))
        {
            await e.Callback(killer);
        }
    }

    public static void RegisterMonsterKillEvent(string name, uint mobId, uint? playerVid,
        Func<IPlayerEntity, Task> callback, Func<IPlayerEntity, bool>? condition = null)
    {
        if (!MonsterKillEvents.TryGetValue(mobId, out var value))
        {
            value = new List<MonsterKillEvent>();
            MonsterKillEvents[mobId] = value;
        }

        value.Add(new MonsterKillEvent
        {
            Name = name, MobId = mobId, PlayerVid = playerVid, Callback = callback, Condition = condition
        });
    }

    public static void RegisterNpcClickEvent(string name, uint npcId, uint? playerVid,
        Func<IPlayerEntity, Task> callback, Func<IPlayerEntity, bool>? condition = null)
    {
        if (!NpcClickEvents.TryGetValue(npcId, out var value))
        {
            value = new List<NpcClickEvent>();
            NpcClickEvents[npcId] = value;
        }

        value.Add(new NpcClickEvent
        {
            Name = name,
            NpcId = npcId,
            PlayerVid = playerVid,
            Callback = callback,
            Condition = condition
        });
    }

    public static void RegisterNpcGiveEvent(string name, uint npcId, uint? playerVid,
        Func<IPlayerEntity, ItemInstance, Task> callback,
        Func<IPlayerEntity, ItemInstance, bool>? condition = null)
    {
        if (!NpcGiveEvents.TryGetValue(npcId, out var value))
        {
            value = new List<NpcGiveEvent>();
            NpcGiveEvents[npcId] = value;
        }

        value.Add(new NpcGiveEvent
        {
            Name = name,
            NpcId = npcId,
            PlayerVid = playerVid,
            Callback = callback,
            Condition = condition
        });
    }

    /// <summary>
    /// Removes every event registered for a given player Vid, across all three event kinds. Quest.Init()
    /// registers into these static dictionaries every time <see cref="QuestManager.InitializePlayer"/> runs
    /// (i.e. on every login AND every bot spawn), but nothing ever removed the previous registrations - so
    /// across a long-running server, entries piled up without bound, each one a closure holding a stale
    /// (possibly disconnected/disposed) <see cref="IPlayerEntity"/>. Once the game's Vid pool recycled a
    /// number, a relogging player or a respawned bot could match several stale conditions at once (since
    /// they all check "vid == some-old-player.Vid" and Vid values get reused), turning a single NPC click
    /// into a bogus multi-choice menu of duplicate options and, worse, invoking a callback bound to a dead
    /// player/connection - this is the root cause behind the client lockup seen when talking to the Horse
    /// Keeper after many bot spawn/despawn cycles this session. Call this both when a player leaves (so
    /// their entries don't linger) and right before re-registering on (re)entry (so a reused Vid starts
    /// clean even if the previous departure wasn't cleanly observed, e.g. bots which skip persistence).
    /// </summary>
    public static void UnregisterPlayer(uint playerVid)
    {
        foreach (var list in NpcClickEvents.Values)
        {
            list.RemoveAll(e => e.PlayerVid == playerVid);
        }

        foreach (var list in NpcGiveEvents.Values)
        {
            list.RemoveAll(e => e.PlayerVid == playerVid);
        }

        foreach (var list in MonsterKillEvents.Values)
        {
            list.RemoveAll(e => e.PlayerVid == playerVid);
        }
    }
}