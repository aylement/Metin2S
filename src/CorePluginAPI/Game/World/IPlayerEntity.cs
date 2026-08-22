using QuantumCore.API.Core.Models;
using QuantumCore.API.Game.Skills;
using QuantumCore.API.Game.Types.Entities;
using QuantumCore.API.Game.Types.Items;
using QuantumCore.API.Game.Types.Monsters;
using QuantumCore.API.Game.Types.Players;
using QuantumCore.API.Game.Types.Skills;

namespace QuantumCore.API.Game.World;

public interface IPlayerEntity : IEntity
{
    long Mana { get; set; }
    string Name { get; }
    IGameConnection Connection { get; }
    PlayerData Player { get; }
    IInventory Inventory { get; }
    IList<Guid> Groups { get; }
    IShop? Shop { get; set; }
    IQuickSlotBar QuickSlotBar { get; }
    IPlayerSkills Skills { get; }
    IQuest? CurrentQuest { get; set; }
    Dictionary<string, IQuest> Quests { get; }
    EAntiFlags AntiFlagClass { get; }
    EAntiFlags AntiFlagGender { get; }

    /// <summary>
    /// Mob proto id of the mount currently ridden, or 0 if not mounted. Sent to clients via
    /// <see cref="QuantumCore.API.Packets.CharacterInfo.MountVnum"/> /
    /// <see cref="QuantumCore.API.Packets.CharacterUpdate.MountVnum"/> - the client swaps the character's
    /// walk/run animation and rendering for the mount's own motion data once this is non-zero (see
    /// PlayerExtensions.SendCharacterAdditional/SendCharacterUpdate).
    /// </summary>
    uint MountVnum { get; }

    void Mount(uint vnum);
    void Unmount();

    /// <summary>
    /// Bitmask of currently-active <see cref="EAffectFlags"/> from active skill buffs (e.g. a weapon aura)
    /// - sent to self and nearby players via <see cref="QuantumCore.API.Packets.CharacterUpdate.Affects"/>/
    /// <see cref="QuantumCore.API.Packets.SpawnCharacter.Affects"/>, which is what actually drives the
    /// on-model visual effect (separately from the buff-icon bar, which uses a dedicated AffectAdd/Remove
    /// packet sent to the affected player only - see PlayerSkills.Use's own doc comment).
    /// </summary>
    ulong ActiveAffects { get; }

    Task LoadAsync();
    Task ReloadPermissionsAsync();
    T? GetQuestInstance<T>() where T : class, IQuest;
    void Respawn(bool town);
    uint CalculateAttackDamage(uint baseDamage);
    uint GetHitRate();
    void AddPoint(EPoint point, int value);
    void SetPoint(EPoint point, uint value);
    Task DropItemAsync(ItemInstance item, byte count);
    Task PickupAsync(IGroundItem groundItem);
    void DropGold(uint amount);
    ItemInstance? GetItem(WindowType window, ushort position);
    bool IsSpaceAvailable(ItemInstance item, WindowType window, ushort position);
    bool IsEquippable(ItemInstance item);
    Task<bool> DestroyItemAsync(ItemInstance item);
    void RemoveItem(ItemInstance item);
    Task SetItemAsync(ItemInstance item, WindowType window, ushort position);
    void Disconnect();
    Task OnDespawnAsync();

    /// <summary>
    /// Persists this player's current state (position, quick slot bar, skills) without despawning them -
    /// unlike <see cref="OnDespawnAsync"/>, safe to call periodically on a still-connected player. Used for
    /// periodic autosave (see <c>Game:AutoSaveIntervalSeconds</c>) so a forceful server termination (a
    /// crash, or an operator killing the process without a graceful shutdown) loses at most one interval's
    /// worth of progress instead of everything since the player's last disconnect.
    /// </summary>
    Task SaveAsync();
    Task CalculatePlayedTimeAsync();
    int GetMobItemRate();
    bool HasUniqueItemEquipped(uint itemProtoId);
    bool HasUniqueGroupItemEquipped(uint itemProtoId);
    int GetPremiumRemainSeconds(EPremiumType type);
    bool IsUsableSkillMotion(ESkill motion);
    Task RefreshGuildAsync();
    void RecalculateStatusPoints();
}