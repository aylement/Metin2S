using System.Collections.Immutable;
using QuantumCore.API.Core.Models;
using QuantumCore.API.Game.Types.Monsters;
using QuantumCore.API.Game.World;
using QuantumCore.Game.Drops;
using QuantumCore.Game.World.Entities;

namespace QuantumCore.Game.Services;

/// <summary>
/// <see cref="Rank"/> - which of common_drop_item.txt's 4 tab-separated columns this entry came from
/// (PAWN/S_PAWN/KNIGHT/S_KNIGHT, matching the real client's <c>MOB_RANK_*</c> enum and this repo's own
/// <see cref="EMonsterLevel"/>). The real server (<c>ITEM_MANAGER::ReadCommonDropItemFile</c>,
/// item_manager_read_tables.cpp) keeps 4 SEPARATE per-rank drop lists and only rolls a killed monster's own
/// rank's list - a BOSS-rank kill gets none of these at all (the file only has 4 columns, ranks 0-3). See
/// <see cref="QuantumCore.Game.Services.DropProvider.CalculateCommonDropItems"/> for where this is applied.
/// </summary>
public record struct CommonDropEntry(byte MinLevel, byte MaxLevel, uint ItemProtoId, float Chance, EMonsterLevel Rank);

public record struct EtcItemDropEntry(uint ItemProtoId, float Multiplier);

public interface IDropProvider
{
    MonsterItemGroup? GetMonsterDropsForMob(uint monsterProtoId);
    DropItemGroup? GetDropItemsGroupForMob(uint monsterProtoId);

    ImmutableArray<EtcItemDropEntry> EtcDrops { get; }

    ImmutableArray<CommonDropEntry> CommonDrops { get; }

    ImmutableArray<LevelItemGroup> LevelDrops { get; }
    (int deltaPercentage, int dropRange) CalculateDropPercentages(IPlayerEntity player, MonsterEntity monster);

    ImmutableArray<ItemInstance> CalculateCommonDropItems(IPlayerEntity player, MonsterEntity monster, int delta,
        int range);

    ImmutableArray<ItemInstance> CalculateDropItemGroupItems(MonsterEntity monster, int delta, int range);

    ImmutableArray<ItemInstance>
        CalculateMobDropItemGroupItems(IPlayerEntity player, MonsterEntity monster, int delta, int range);

    ImmutableArray<ItemInstance> CalculateLevelDropItems(IPlayerEntity player, MonsterEntity monster, int delta,
        int range);

    ImmutableArray<ItemInstance> CalculateEtcDropItems(MonsterEntity monster, int delta, int range);
    ImmutableArray<ItemInstance> CalculateMetinDropItems(MonsterEntity monsterEntity, int delta, int range);
}