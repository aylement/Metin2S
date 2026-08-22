using QuantumCore.API.Game.Types;
using QuantumCore.API.Game.Types.Players;
using QuantumCore.API.Game.Types.Skills;

namespace QuantumCore.API.Core.Models;

public sealed class PlayerData
{
    public uint Id { get; set; }
    public Guid AccountId { get; set; }
    public string Name { get; set; } = "";
    public EPlayerClassGendered PlayerClass { get; set; }
    public ESkillGroup SkillGroup { get; set; }
    public ulong PlayTime { get; set; }
    public byte Level { get; set; } = 1;
    public uint Experience { get; set; }
    public uint Gold { get; set; }
    public byte St { get; set; }
    public byte Ht { get; set; }
    public byte Dx { get; set; }
    public byte Iq { get; set; }
    public int PositionX { get; set; }
    public int PositionY { get; set; }
    public long Health { get; set; }
    public long Mana { get; set; }
    public long Stamina { get; set; }
    public uint BodyPart { get; set; }
    public uint HairPart { get; set; }
    public uint GivenStatusPoints { get; set; }
    public uint AvailableStatusPoints { get; set; }
    public uint MinWeaponDamage { get; set; }
    public uint MaxWeaponDamage { get; set; }
    public uint MinAttackDamage { get; set; }
    public uint MaxAttackDamage { get; set; }
    public uint MaxHp { get; set; }
    public uint MaxSp { get; set; }
    public EEmpire Empire { get; set; }
    public byte Slot { get; set; }
    public uint AvailableSkillPoints { get; set; }
    public uint? GuildId { get; set; }

    /// <summary>
    /// 0 = no horse unlocked yet, 1-30 = horse level (matches HORSE_MAX_LEVEL in the original game's
    /// horse_rider.h). Deliberately NOT persisted to the database yet (no matching column on the EF
    /// Player entity, see src/Data/Game.Persistence/Entities/Player.cs) - simplest possible first cut,
    /// resets to 0 on every login/restart. A first, easy-to-extend iteration; see MountCommand.cs and
    /// SetHorseLevelCommand.cs for how it's used.
    /// </summary>
    public byte HorseLevel { get; set; }
}
