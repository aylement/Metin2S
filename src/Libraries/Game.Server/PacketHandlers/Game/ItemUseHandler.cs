using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using QuantumCore.API;
using QuantumCore.API.Core.Models;
using QuantumCore.API.Extensions;
using QuantumCore.API.Game.Types.Items;
using QuantumCore.API.Game.Types.Skills;
using QuantumCore.API.Game.World;
using QuantumCore.API.Packets;
using QuantumCore.API.PluginTypes;
using QuantumCore.Core.Utils;
using QuantumCore.Game.Extensions;
using QuantumCore.Game.Items;

namespace QuantumCore.Game.PacketHandlers.Game;

internal class ItemUseHandler : IGamePacketHandler<ItemUse>
{
    private readonly IItemManager _itemManager;
    private readonly IItemRepository _itemRepository;
    private readonly ICacheManager _cacheManager;
    private readonly ILogger<ItemUseHandler> _logger;
    private readonly SkillsOptions _skillsOptions;

    public ItemUseHandler(IItemManager itemManager, IItemRepository itemRepository, ICacheManager cacheManager,
        ILogger<ItemUseHandler> logger, IOptions<GameOptions> gameOptions)
    {
        _itemManager = itemManager;
        _itemRepository = itemRepository;
        _cacheManager = cacheManager;
        _logger = logger;
        _skillsOptions = gameOptions.Value.Skills;
    }

    public async Task ExecuteAsync(GamePacketContext<ItemUse> ctx, CancellationToken token = default)
    {
        var player = ctx.Connection.Player;
        if (player is null)
        {
            ctx.Connection.Close();
            return;
        }

        _logger.LogDebug("Use item {Window},{Position}", ctx.Packet.Window, ctx.Packet.Position);

        var item = player.GetItem(ctx.Packet.Window, ctx.Packet.Position);
        if (item is null)
        {
            _logger.LogDebug("Used item not found!");
            return;
        }

        var itemProto = _itemManager.GetItem(item.ItemId);
        if (itemProto is null)
        {
            _logger.LogDebug("Cannot find item proto {ItemId}", item.ItemId);
            return;
        }

        if (ctx.Packet.Window == WindowType.INVENTORY && ctx.Packet.Position >= player.Inventory.Size)
        {
            player.RemoveItem(item);
            if (await player.Inventory.PlaceItemAsync(item))
            {
                player.SendRemoveItem(ctx.Packet.Window, ctx.Packet.Position);
                player.SendItem(item);
                player.SendCharacterUpdate();
            }
            else
            {
                await player.SetItemAsync(item, ctx.Packet.Window, ctx.Packet.Position);
                player.SendChatInfo("Cannot unequip item if the inventory is full");
            }
        }
        else if (player.IsEquippable(item))
        {
            var wearSlot = player.Inventory.EquipmentWindow.GetWearPosition(_itemManager, item.ItemId);

            if (wearSlot <= ushort.MaxValue)
            {
                var item2 = player.Inventory.EquipmentWindow.GetItem((ushort)wearSlot);

                if (item2 is not null)
                {
                    player.RemoveItem(item);
                    player.RemoveItem(item2);
                    if (await player.Inventory.PlaceItemAsync(item2))
                    {
                        player.SendRemoveItem(ctx.Packet.Window, (ushort)wearSlot);
                        player.SendRemoveItem(ctx.Packet.Window, ctx.Packet.Position);
                        await player.SetItemAsync(item, ctx.Packet.Window, (ushort)wearSlot);
                        await player.SetItemAsync(item2, ctx.Packet.Window, ctx.Packet.Position);
                        player.SendItem(item);
                        player.SendItem(item2);
                    }
                    else
                    {
                        await player.SetItemAsync(item, ctx.Packet.Window, ctx.Packet.Position);
                        await player.SetItemAsync(item2, ctx.Packet.Window, (ushort)wearSlot);
                        player.SendChatInfo("Cannot swap item if the inventory is full");
                    }
                }
                else
                {
                    player.RemoveItem(item);
                    await player.SetItemAsync(item, WindowType.INVENTORY, (ushort)wearSlot);
                    player.SendRemoveItem(ctx.Packet.Window, ctx.Packet.Position);
                    player.SendItem(item);
                }
            }
        }
        // Skills related
        // note: Should maybe create an ItemUseHandler<ItemId> for this ? Similarly to the commands and packet handlers
        else if (itemProto.IsType(EItemType.SKILLBOOK))
        {
            var skillId = itemProto.Id == _skillsOptions.GenericSkillBookId
                ? itemProto.Sockets[0]
                : itemProto.Values[0];

            if (!Enum.TryParse<ESkill>(skillId.ToString(), out var skill))
            {
                _logger.LogWarning("Skill with Id({SkillId}) not defined", skillId);
                return;
            }

            if (!player.Skills.LearnSkillByBook(skill)) return;

            var currentTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var delay = CoreRandom.GenerateInt32(_skillsOptions.SkillBookDelayMin,
                _skillsOptions.SkillBookDelayMax + 1);

            player.Skills.SetSkillNextReadTime(skill, (int)currentTime + delay);
            player.RemoveItem(item);
            player.SendRemoveItem(ctx.Packet.Window, ctx.Packet.Position);
        }
        else if (itemProto.Id == _skillsOptions.SoulStoneId)
        {
            player.RemoveItem(item);
            player.SendRemoveItem(ctx.Packet.Window, ctx.Packet.Position);
        }
        // Single-use "open for a random reward" boxes (e.g. Esoteric Leader's Box) - see BoxDefinitions.
        else if (BoxDefinitions.Boxes.TryGetValue(itemProto.Id, out var rewards))
        {
            var reward = rewards[CoreRandom.GenerateInt32(0, rewards.Length)];
            var description = await reward.GrantAsync(player, _itemManager, _itemRepository);

            if (description is null)
            {
                // Only ItemBoxReward can fail this way (no free inventory slot) - leave the box
                // unconsumed so the player can free up space and try again, same as every other
                // "couldn't fit it" case in this handler.
                player.SendChatInfo("Not enough inventory space to open this box");
                return;
            }

            await ConsumeOneAsync(player, item, ctx.Packet.Window, ctx.Packet.Position);
            player.SendChatInfo($"{itemProto.TranslatedName}: you received {description}");
        }
    }

    /// <summary>
    /// Removes exactly one unit of a (possibly stacked) item - decrementing the stack if more than one
    /// remains, or fully removing it otherwise. Mirrors PlayerEntity.DropItemAsync's own count==count vs
    /// count-=count split, which isn't exposed as a standalone helper there.
    /// </summary>
    private async Task ConsumeOneAsync(IPlayerEntity player, ItemInstance item, WindowType window, ushort position)
    {
        if (item.Count <= 1)
        {
            player.RemoveItem(item);
            player.SendRemoveItem(window, position);
            await _itemRepository.DeletePlayerItemAsync(_cacheManager, item.PlayerId, item.ItemId);
        }
        else
        {
            item.Count -= 1;
            await item.PersistAsync(_itemRepository);
            player.SendItem(item);
        }
    }
}