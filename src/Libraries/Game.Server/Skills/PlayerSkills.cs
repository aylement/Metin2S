using System.Collections.Concurrent;
using System.Collections.Immutable;
using EnumsNET;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using QuantumCore.API;
using QuantumCore.API.Core.Models;
using QuantumCore.API.Game.Skills;
using QuantumCore.API.Game.Types.Combat;
using QuantumCore.API.Game.Types.Entities;
using QuantumCore.API.Game.Types.Players;
using QuantumCore.API.Game.Types.Skills;
using QuantumCore.API.Game.World;
using QuantumCore.API.Packets.Skills;
using QuantumCore.Core.Utils;
using QuantumCore.Game.Extensions;
using QuantumCore.Game.Persistence;
using QuantumCore.Game.World.Entities;
using static QuantumCore.API.Game.Types.Skills.ESkillLevelUtils;

namespace QuantumCore.Game.Skills;

public class PlayerSkills : IPlayerSkills
{
    private readonly ConcurrentDictionary<ESkill, Skill>
        _skills = new(); //todo: probably no need for concurrent variant

    /// <summary>Per-skill "ready again at" timestamp - in-memory only, resets on relog like a real cooldown
    /// would visually reset anyway (the client doesn't persist a countdown across sessions either).</summary>
    private readonly Dictionary<ESkill, DateTime> _cooldowns = new();

    /// <summary>A "primed strike" left behind by a SPLASH damage skill with a self-buff secondary effect
    /// (e.g. Dash: HP splash damage + MOV_SPEED) cast with nothing in range to hit - see Use()'s own
    /// comment for why this exists. Checked every tick via <see cref="TryTriggerPendingSplash"/>.</summary>
    private (SkillData Proto, Dictionary<string, double> Vars, ESkill SkillId, DateTime Deadline)? _pendingSplash;

    private readonly ILogger<PlayerSkills> _logger;
    private readonly PlayerEntity _player;
    private readonly IDbPlayerSkillsRepository _repository;
    private readonly ISkillManager _skillManager;
    private readonly SkillsOptions _skillsOptions;

    public const int SKILL_MAX_NUM = byte.MaxValue;
    public const ESkillLevel SKILL_MAX_LEVEL = ESkillLevel.PERFECT_MASTER_P;
    public const int SKILL_COUNT = 6;
    public const int JOB_MAX_NUM = 4;
    public const int SKILL_GROUP_MAX_NUM = 2;
    public const int MINIMUM_LEVEL = 5;
    public const int MINIMUM_LEVEL_SUB_SKILLS = 10;
    public const ESkillLevel MINIMUM_SKILL_LEVEL_UPGRADE = ESkillLevel.NORMAL17;

    #region Static Skill Data

#pragma warning disable CA1814 // no multi dimensional arrays - is okay here
    private static readonly ESkill[,,] SkillList = new ESkill[JOB_MAX_NUM, SKILL_GROUP_MAX_NUM, SKILL_COUNT]
#pragma warning restore CA1814
    {
        // Warrior
        {
            {
                ESkill.THREE_WAY_CUT, ESkill.SWORD_SPIN, ESkill.BERSERKER_FURY, ESkill.AURA_OF_THE_SWORD, ESkill.DASH,
                ESkill.LIFE
            },
            { ESkill.SHOCKWAVE, ESkill.BASH, ESkill.STUMP, ESkill.STRONG_BODY, ESkill.SWORD_STRIKE, ESkill.SWORD_ORB }
        },
        // Ninja
        {
            {
                ESkill.AMBUSH, ESkill.FAST_ATTACK, ESkill.ROLLING_DAGGER, ESkill.STEALTH, ESkill.POISONOUS_CLOUD,
                ESkill.INSIDIOUS_POISON
            },
            {
                ESkill.REPETITIVE_SHOT, ESkill.ARROW_SHOWER, ESkill.FIRE_ARROW, ESkill.FEATHER_WALK,
                ESkill.POISON_ARROW,
                ESkill.SPARK
            }
        },
        // Sura
        {
            {
                ESkill.FINGER_STRIKE, ESkill.DRAGON_SWIRL, ESkill.ENCHANTED_BLADE, ESkill.FEAR, ESkill.ENCHANTED_ARMOR,
                ESkill.DISPEL
            },
            {
                ESkill.DARK_STRIKE, ESkill.FLAME_STRIKE, ESkill.FLAME_SPIRIT, ESkill.DARK_PROTECTION,
                ESkill.SPIRIT_STRIKE,
                ESkill.DARK_ORB
            }
        },
        // Shaman
        {
            {
                ESkill.FLYING_TALISMAN, ESkill.SHOOTING_DRAGON, ESkill.DRAGON_ROAR, ESkill.BLESSING, ESkill.REFLECT,
                ESkill.DRAGON_AID
            },
            {
                ESkill.LIGHTNING_THROW, ESkill.SUMMON_LIGHTNING, ESkill.LIGHTNING_CLAW, ESkill.CURE, ESkill.SWIFTNESS,
                ESkill.ATTACK_UP
            }
        }
    };

    private static readonly ImmutableArray<ESkill> PassiveSkillIds =
    [
        ESkill.LEADERSHIP,
        ESkill.COMBO,
        ESkill.MINING,
        ESkill.LANGUAGE_SHINSOO,
        ESkill.LANGUAGE_CHUNJO,
        ESkill.LANGUAGE_JINNO,
        ESkill.POLYMORPH,
        ESkill.HORSE_RIDING,
        ESkill.HORSE_SUMMON,
        ESkill.HORSE_WILD_ATTACK,
        ESkill.HORSE_CHARGE,
        ESkill.HORSE_ESCAPE,
        ESkill.HORSE_WILD_ATTACK_RANGE,
        ESkill.ADD_HP,
        ESkill.PENETRATION_RESISTANCE
    ];

    #endregion

    public PlayerSkills(ILogger<PlayerSkills> logger, PlayerEntity player, IDbPlayerSkillsRepository repository,
        ISkillManager skillManager, IOptions<SkillsOptions> skillsOptions)
    {
        ArgumentNullException.ThrowIfNull(skillsOptions);
        _logger = logger;
        _player = player;
        _repository = repository;
        _skillManager = skillManager;
        _skillsOptions = skillsOptions.Value;
    }

    public async Task LoadAsync()
    {
        if (HasChosenSkillGroup())
        {
            AssignDefaultActiveSkills();
        }

        AssignDefaultPassiveSkills();

        var skills = await _repository.GetPlayerSkillsAsync(_player.Player.Id);

        foreach (var skill in skills)
        {
            _skills[skill.SkillId] = skill ?? throw new InvalidOperationException();
        }
    }

    public async Task PersistAsync()
    {
        foreach (var skill in _skills.Values)
        {
            await _repository.SavePlayerSkillAsync(skill);
        }
    }

    public ISkill? this[ESkill skillId] => _skills.TryGetValue(skillId, out var skill) ? skill : null;

    public void SetSkillGroup(ESkillGroup skillGroup)
    {
        if (!skillGroup.IsDefined() && skillGroup != 0) return;
        if (_player.GetPoint(EPoint.LEVEL) < MINIMUM_LEVEL) return;

        // todo: prevent changing skill group in certain situations

        _player.Player.SkillGroup = skillGroup;

        AssignDefaultActiveSkills();

        _player.Connection.Send(new ChangeSkillGroup { SkillGroup = skillGroup });
    }

    public void ClearSkills()
    {
        var points = _player.GetPoint(EPoint.LEVEL) < MINIMUM_LEVEL
            ? 0
            : (MINIMUM_LEVEL - 1) + (_player.GetPoint(EPoint.LEVEL) - MINIMUM_LEVEL) - _player.GetPoint(EPoint.SKILL);
        _player.SetPoint(EPoint.SKILL, points);

        ResetSkills();
    }

    public void ClearSubSkills()
    {
        var points = _player.GetPoint(EPoint.LEVEL) < MINIMUM_LEVEL_SUB_SKILLS
            ? 0
            : (_player.GetPoint(EPoint.LEVEL) - (MINIMUM_LEVEL_SUB_SKILLS - 1)) - _player.GetPoint(EPoint.SUB_SKILL);

        _player.SetPoint(EPoint.SUB_SKILL, points);

        ResetSubSkills();
    }

    private void ResetSkills()
    {
        // store subskills in a temporary variable, clear the dictionary and then restore the subskills
        var subSkills = _skills
            .Where(sk => PassiveSkillIds.Contains(sk.Key))
            .ToDictionary(sk => sk.Key, sk => sk.Value);

        _skills.Clear();

        if (HasChosenSkillGroup())
        {
            AssignDefaultActiveSkills();
        }

        foreach (var subSkill in subSkills)
        {
            _skills[subSkill.Key] = subSkill.Value;
        }

        SendSkillLevelsPacket();
    }

    private void ResetSubSkills()
    {
        // Assign default values to passive skills
        AssignDefaultPassiveSkills();

        SendSkillLevelsPacket();
    }

    public void Reset(ESkill skillId)
    {
        if (!skillId.IsDefined())
        {
            return;
        }

        if (!_skills.TryGetValue(skillId, out var skill)) return;

        var effectiveLevelForRestore = skill.Level < MINIMUM_SKILL_LEVEL_UPGRADE
            ? skill.Level
            : MINIMUM_SKILL_LEVEL_UPGRADE;

        _player.AddPoint(EPoint.SKILL, (byte)effectiveLevelForRestore);

        skill.Level = ESkillLevel.UNLEARNED;
        skill.MasterType = ESkillMasterType.NORMAL;
        skill.NextReadTime = 0;

        SendSkillLevelsPacket();
    }

    public void SetLevel(ESkill skillId, ESkillLevel level)
    {
        if (!skillId.IsDefined())
        {
            return;
        }

        if (!_skills.TryGetValue(skillId, out var skill)) return;

        skill.Level = Min(SKILL_MAX_LEVEL, level);

        skill.MasterType = level switch
        {
            >= SKILL_MAX_LEVEL => ESkillMasterType.PERFECT_MASTER,
            >= ESkillLevel.GRAND_MASTER_G1 => ESkillMasterType.GRAND_MASTER,
            >= ESkillLevel.MASTER_M1 => ESkillMasterType.MASTER,
            _ => ESkillMasterType.NORMAL
        };

        // Reset reads required when new master type is learned
        switch (skill)
        {
            case { Level: ESkillLevel.MASTER_M1, ReadsRequired: 0, MasterType: ESkillMasterType.MASTER }:
            case { Level: ESkillLevel.GRAND_MASTER_G1, ReadsRequired: 0, MasterType: ESkillMasterType.GRAND_MASTER }:
                skill.ReadsRequired = 1;
                break;
        }
    }

    public void SkillUp(ESkill skillId, ESkillLevelMethod method = ESkillLevelMethod.POINT)
    {
        if (!skillId.IsDefined())
        {
            _logger.LogWarning("Invalid skill id: {SkillId}", skillId);
            return;
        }

        var proto = _skillManager.GetSkill(skillId);
        if (proto is null)
        {
            _logger.LogWarning("Skill not found: {SkillId}", skillId);
            return;
        }

        if (!proto.Id.IsDefined())
        {
            _logger.LogWarning("Invalid skill id: {SkillId}", skillId);
            return;
        }

        var skill = _skills.TryGetValue(proto.Id, out var playerSkill) ? playerSkill : null;
        if (skill is null)
        {
            _logger.LogWarning("Skill not found: {SkillId}", skillId);
            return;
        }

        if (!IsLearnableSkill(skillId))
        {
            _player.SendChatInfo("You cannot learn this skill.");
            return;
        }

        if (proto.Type != 0)
        {
            switch (skill.MasterType)
            {
                case ESkillMasterType.GRAND_MASTER:
                    if (method != ESkillLevelMethod.QUEST) return;
                    break;
                case ESkillMasterType.PERFECT_MASTER:
                    return;
            }
        }

        switch (method)
        {
            case ESkillLevelMethod.POINT when skill.MasterType != ESkillMasterType.NORMAL:
            case ESkillLevelMethod.POINT
                when (proto.Flags & ESkillFlags.DISABLE_BY_POINT_UP) == ESkillFlags.DISABLE_BY_POINT_UP:
            case ESkillLevelMethod.BOOK when proto.Type != 0 && skill.MasterType != ESkillMasterType.MASTER:
                return;
        }

        if (_player.GetPoint(EPoint.LEVEL) < proto.LevelLimit) return;

        if (proto.PrerequisiteSkillVnum > 0)
        {
            var prerequisiteSkillLevel = (ESkillLevel)proto.PrerequisiteSkillLevel;
            if (skill.MasterType == ESkillMasterType.NORMAL && GetSkillLevel(proto.Id) < prerequisiteSkillLevel)
            {
                _player.SendChatInfo("You need to learn the prerequisite skill first.");
                return;
            }
        }

        if (!HasChosenSkillGroup()) return;

        if (method == ESkillLevelMethod.POINT)
        {
            EPoint idx; // enum

            switch (proto.Type)
            {
                case ESkillCategoryType.PASSIVE_SKILLS:
                    idx = EPoint.SUB_SKILL;
                    break;
                case ESkillCategoryType.WARRIOR_SKILLS: // warrior
                case ESkillCategoryType.NINJA_SKILLS: // ninja
                case ESkillCategoryType.SURA_SKILLS: // sura
                case ESkillCategoryType.SHAMAN_SKILLS: // shaman
                    idx = EPoint.SKILL;
                    break;
                case ESkillCategoryType.HORSE_SKILLS:
                    idx = EPoint.HORSE_SKILL;
                    break;
                default:
                    _logger.LogWarning("Invalid skill type: {SkillType}", proto.Type);
                    return;
            }

            if (_player.GetPoint(idx) < 1) return;

            _player.AddPoint(idx, -1);
        }

        SetLevel(proto.Id, GetSkillLevel(proto.Id) + 1);

        if (proto.Type != ESkillCategoryType.PASSIVE_SKILLS)
        {
            switch (skill.MasterType)
            {
                case ESkillMasterType.NORMAL:
                    if (GetSkillLevel(proto.Id) >= MINIMUM_SKILL_LEVEL_UPGRADE)
                    {
                        //todo: implement reset scroll quest flag
                        var effectiveLevel = Min(ESkillLevel.MASTER_M1, GetSkillLevel(proto.Id));
                        var levelsUnder = ESkillLevel.MASTER_M2 - effectiveLevel;
                        var random = CoreRandom.GenerateInt32(1, levelsUnder + 1);
                        if (random == 1)
                        {
                            SetLevel(proto.Id, ESkillLevel.MASTER_M1);
                        }
                    }

                    break;
                case ESkillMasterType.MASTER:
                    if (GetSkillLevel(proto.Id) >= ESkillLevel.GRAND_MASTER_G1)
                    {
                        var effectiveLevel = Min(ESkillLevel.GRAND_MASTER_G1, GetSkillLevel(proto.Id));
                        var levelsUnder = ESkillLevel.GRAND_MASTER_G2 - effectiveLevel;
                        var random = CoreRandom.GenerateInt32(1, levelsUnder + 1);
                        if (random == 1)
                        {
                            SetLevel(proto.Id, ESkillLevel.GRAND_MASTER_G1);
                        }
                    }

                    break;
                case ESkillMasterType.GRAND_MASTER:
                    if (GetSkillLevel(proto.Id) >= ESkillLevel.PERFECT_MASTER_P)
                    {
                        SetLevel(proto.Id, ESkillLevel.PERFECT_MASTER_P);
                    }

                    break;
            }
        }

        _logger.LogInformation("Skill up: {SkillId} ({Name}) [{Master}] -> {Level} ({LevelName})", proto.Id, proto.Name,
            skill.MasterType, (byte)GetSkillLevel(proto.Id), GetSkillLevel(proto.Id).GetName());

        _player.SendPoints();
        SendSkillLevelsPacket();
    }

    private bool IsLearnableSkill(ESkill skillId)
    {
        var proto = _skillManager.GetSkill(skillId);
        if (proto is null)
        {
            return false;
        }

        if (GetSkillLevel(skillId) >= SKILL_MAX_LEVEL) return false;

        if (proto.Type == ESkillCategoryType.PASSIVE_SKILLS)
        {
            return GetSkillLevel(skillId) < (ESkillLevel)proto.MaxLevel;
        }

        if (proto.Type == ESkillCategoryType.HORSE_SKILLS)
        {
            return skillId != ESkill.HORSE_WILD_ATTACK_RANGE ||
                   _player.Player.PlayerClass.GetClass() == EPlayerClass.NINJA;
        }

        if (!HasChosenSkillGroup()) return false;

        // Same normalization bug as CanUse() above: comparing against the raw EPlayerClassGendered value
        // (0-7) instead of the base EPlayerClass (0-3, via .GetClass()) only coincidentally worked for the
        // first four gendered values (WARRIOR_MALE=0..SHAMAN_FEMALE=3, which happen to line up numerically
        // with WARRIOR=0..SHAMAN=3) - for the other four (WARRIOR_FEMALE=4 and up) this was never equal to
        // anything, so SkillUp/LearnSkillByBook has likely never worked for those genders since this
        // existed. Found while investigating an unrelated CanUse() crash on a female warrior test
        // character - same root confusion, different symptom (silently-always-false here, vs. an
        // out-of-bounds array index there).
        return (int)proto.Type - 1 == (byte)_player.Player.PlayerClass.GetClass();
    }

    private ESkillLevel GetSkillLevel(ESkill skillId)
    {
        if (!skillId.IsDefined() || !_skills.TryGetValue(skillId, out var skill))
        {
            return ESkillLevel.UNLEARNED;
        }

        return Min(SKILL_MAX_LEVEL, skill.Level);
    }

    public bool CanUse(ESkill skillId)
    {
        if (skillId == 0) return false;

        var skillGroup = _player.Player.SkillGroup;

        // Same bug class as SkillUp/AssignDefaultActiveSkills elsewhere in this file: skillGroup.IsDefined()
        // alone is true even for ESkillGroup.UNKNOWN (0, what every player starts with), so a fresh
        // character whose branch pick never actually landed (e.g. /setjob run before reaching the level-5
        // minimum, which silently no-ops) would compute (byte)UNKNOWN - 1 = -1 as an array index below -
        // live-confirmed via a real IndexOutOfRangeException crashing the connection on /skillup and
        // /all_skills_master alike. Must use HasChosenSkillGroup() here too, not the raw IsDefined() check.
        if (HasChosenSkillGroup())
        {
            // A second, separate real bug alongside the one above: PlayerClass is EPlayerClassGendered
            // (8 values - WARRIOR_MALE=0 through SHAMAN_MALE=7), but SkillList's first dimension is
            // JOB_MAX_NUM=4 (base classes only). AssignDefaultActiveSkills() already correctly normalizes
            // via .GetClass() before indexing; this method didn't, so any of the four "second half"
            // gendered values (WARRIOR_FEMALE and up) indexed straight past the array's end - live-
            // confirmed via a real IndexOutOfRangeException crashing the connection on a female warrior
            // test character, independent of the SkillGroup fix above (both bugs hit the same line).
            var playerClass = _player.Player.PlayerClass.GetClass();
            for (var i = 0; i < SKILL_COUNT; i++)
            {
                if (SkillList[(int)playerClass, (byte)skillGroup - 1, i] == skillId)
                {
                    return true;
                }
            }
        }

        // todo: horse riding check

        switch (skillId)
        {
            case ESkill.LEADERSHIP:
            case ESkill.COMBO:
            case ESkill.MINING:
            case ESkill.LANGUAGE_SHINSOO:
            case ESkill.LANGUAGE_CHUNJO:
            case ESkill.LANGUAGE_JINNO:
            case ESkill.POLYMORPH:
            case ESkill.HORSE_RIDING:
            case ESkill.HORSE_SUMMON:
            case ESkill.GUILD_EYE:
            case ESkill.GUILD_BLOOD:
            case ESkill.GUILD_BLESS:
            case ESkill.GUILD_SEONGHWI:
            case ESkill.GUILD_ACCELERATION:
            case ESkill.GUILD_BUNNO:
            case ESkill.GUILD_JUMUN:
            case ESkill.GUILD_TELEPORT:
            case ESkill.GUILD_DOOR:
                return true;
        }

        return false;
    }

    public void Send()
    {
        SendSkillLevelsPacket();
    }

    public bool LearnSkillByBook(ESkill skillId)
    {
        var proto = _skillManager.GetSkill(skillId);
        if (proto is null)
        {
            return false;
        }

        if (!IsLearnableSkill(skillId))
        {
            _player.SendChatInfo("You cannot learn this skill.");
            return false;
        }

        if (_player.GetPoint(EPoint.EXPERIENCE) < _skillsOptions.SkillBookNeededExperience)
        {
            _player.SendChatInfo("Not enough experience.");
            return false;
        }

        var skill = _skills.TryGetValue(skillId, out var playerSkill) ? playerSkill : null;
        if (skill is null)
        {
            _logger.LogWarning("Skill not found: {SkillId}", skillId);
            return false;
        }

        if (proto.Type != 0)
        {
            if (skill.MasterType != ESkillMasterType.MASTER)
            {
                _player.SendChatInfo("You cannot learn this skill.");
                return false;
            }
        }

        var currentTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        if (currentTime < skill.NextReadTime)
        {
            _player.SendChatInfo(
                $"You cannot read this skill book yet. {skill.NextReadTime - currentTime} seconds to wait.");
            return false;
        }

        _player.AddPoint(EPoint.EXPERIENCE, -_skillsOptions.SkillBookNeededExperience);

        var previousLevel = skill.Level;

        var readSuccess = CoreRandom.GenerateInt32(1, 3) == 1;

        if (readSuccess)
        {
            if (skill.ReadsRequired - 1 == 0)
            {
                SkillUp(skillId, ESkillLevelMethod.BOOK);
                skill.ReadsRequired = skill.Level switch
                {
                    ESkillLevel.MASTER_M2 => 2,
                    ESkillLevel.MASTER_M3 => 3,
                    ESkillLevel.MASTER_M4 => 4,
                    ESkillLevel.MASTER_M5 => 5,
                    ESkillLevel.MASTER_M6 => 6,
                    ESkillLevel.MASTER_M7 => 7,
                    ESkillLevel.MASTER_M8 => 8,
                    ESkillLevel.MASTER_M9 => 9,
                    ESkillLevel.MASTER_M10 => 10,
                    ESkillLevel.GRAND_MASTER_G1 => 1,
                    _ => 0
                };
            }
            else
            {
                skill.ReadsRequired = Math.Max(1, skill.ReadsRequired - 1);
            }
        }

        if (previousLevel != skill.Level)
        {
            _player.SendChatInfo($"You have learned the skill.");
        }
        else
        {
            _player.SendChatInfo(readSuccess
                ? $"You have learned the skill book. {skill.ReadsRequired} books left."
                : "Failed to read the skill book.");
        }

        return true;
    }

    public void SetSkillNextReadTime(ESkill skillId, int time)
    {
        if (!skillId.IsDefined())
        {
            return;
        }

        if (!_skills.TryGetValue(skillId, out var skill)) return;

        skill.NextReadTime = time;
    }

    /// <summary>
    /// Actually casts a learned active skill - the piece <see cref="QuantumCore.Game.PacketHandlers.Game.UseSkillHandler"/>
    /// was entirely missing (it only logged the request and did nothing). Evaluates the real formulas from
    /// skilltable.txt live via <see cref="SkillFormula"/> (the original server precomputes these into
    /// lookup tables offline instead - not available here, so this evaluates the formula text directly at
    /// cast time, same real numbers, computed on demand) using variable names matching the original
    /// engine's own binding (<c>char_skill.cpp</c>'s <c>SetPointVar</c> calls): <c>k</c> (skill mastery,
    /// approximated here as <c>level / 40</c> - the real engine derives it from a precomputed per-level
    /// power table this repo doesn't have), <c>lv/str/dex/con/iq</c> (the caster's own points), and
    /// <c>atk</c> (<see cref="Entity.CalculateAttackPower"/>, the same pre-defense attack-power computation
    /// MeleeAttack itself already uses).
    ///
    /// A real client always plays its OWN cast/swing animation locally the instant the player presses the
    /// skill key, before this packet even reaches the server - unlike bots (which have no client and must
    /// fake movement/attack via broadcast packets, an investigation that hit a wall this session), so
    /// animation is not this method's concern at all; it only needs to compute and apply the real effect.
    /// </summary>
    public IEnumerable<ESkill> GetUsableActiveSkillIds()
    {
        foreach (var (skillId, skill) in _skills)
        {
            if (skill.Level == ESkillLevel.UNLEARNED) continue;

            var proto = _skillManager.GetSkill(skillId);
            if (proto is null) continue;

            // Mirrors Use()'s own "has anything to actually do" check - a skill with no skilltable.txt
            // row worth of effect (e.g. LIFE/SWORD_ORB) would just refuse with "no active effect".
            if (IsNoneOrEmpty(proto.PointOn) && IsNoneOrEmpty(proto.PointOn2)) continue;

            yield return skillId;
        }
    }

    public void Use(ESkill skillId, uint targetVid)
    {
        if (!skillId.IsDefined())
        {
            return;
        }

        if (!_skills.TryGetValue(skillId, out var skill) || skill.Level == ESkillLevel.UNLEARNED)
        {
            _player.SendChatInfo("You haven't learned this skill.");
            return;
        }

        if (!CanUse(skillId))
        {
            _player.SendChatInfo("You cannot use this skill.");
            return;
        }

        var proto = _skillManager.GetSkill(skillId);
        if (proto is null)
        {
            _logger.LogWarning("Skill data not found for {SkillId}", skillId);
            return;
        }

        var hasPrimaryEffect = !IsNoneOrEmpty(proto.PointOn);
        var hasSecondaryEffect = !IsNoneOrEmpty(proto.PointOn2);
        if (!hasPrimaryEffect && !hasSecondaryEffect)
        {
            // No skilltable.txt row's worth of executable effect at all (e.g. LIFE/SWORD_ORB, which have
            // no row in skilltable.txt in the first place) - a passive-in-practice skill, nothing to cast.
            _player.SendChatInfo("This skill has no active effect.");
            return;
        }

        if (_cooldowns.TryGetValue(skillId, out var readyAt) && readyAt > DateTime.UtcNow)
        {
            _player.SendChatInfo(
                $"{proto.Name} is not ready yet ({(readyAt - DateTime.UtcNow).TotalSeconds:0.#}s left).");
            return;
        }

        var target = targetVid != 0 ? _player.Map?.GetEntity(targetVid) : null;
        var isDamageSkill = hasPrimaryEffect && string.Equals(proto.PointOn, "HP", StringComparison.OrdinalIgnoreCase);

        // A SPLASH-flagged skill (e.g. Dash) can legitimately be cast without any unit locked - live-tested
        // and confirmed the real client really does send TargetVid=0 for one of these even with a monster
        // visibly targeted, presumably resolving it client-side as a ground/area effect around the caster
        // rather than a target-lock. Fall back to the nearest monster within the skill's own real
        // SplashRange (skilltable.txt - 200 for Dash, ~2m) rather than an artificially widened radius: an
        // earlier version searched out to 600 to make a distant cast "find" something, but that let Dash
        // land its AOE like a ranged nuke, which isn't the real mechanic - confirmed against the real
        // client behavior the user described: Dash only lands its melee-range AOE when you're genuinely
        // standing next to a target, exactly like SplashRange implies.
        if (target is null && isDamageSkill && (proto.Flags & ESkillFlags.SPLASH) == ESkillFlags.SPLASH)
        {
            target = FindNearestMonster(proto.SplashRange);
        }

        // A damage skill with no other effect genuinely has nothing to do without a target - refuse it as
        // before. But a skill that pairs a melee-range hit with a self-only secondary buff (Dash: SPLASH
        // damage + MOV_SPEED) should still grant that buff even when nothing was close enough to hit -
        // skilltable.txt's two effects aren't conditional on each other, and this matches the real skill:
        // casting Dash always gives the movement-speed rush, landing the AOE only requires actually being
        // in melee range when you do. Falls through to skip just the primary (damage) effect below instead
        // of aborting the whole cast (no SP spent, no cooldown, no buff either) when only the damage half
        // whiffs.
        var canHitTarget = target is not null;
        if (isDamageSkill && !canHitTarget && !hasSecondaryEffect)
        {
            _player.SendChatInfo("No target.");
            return;
        }

        var vars = new Dictionary<string, double>
        {
            ["k"] = (int)skill.Level / (double)SKILL_MAX_LEVEL,
            ["lv"] = _player.GetPoint(EPoint.LEVEL),
            ["str"] = _player.GetPoint(EPoint.ST),
            ["dex"] = _player.GetPoint(EPoint.DX),
            ["con"] = _player.GetPoint(EPoint.HT),
            ["iq"] = _player.GetPoint(EPoint.IQ),
            ["atk"] = target is not null ? _player.CalculateAttackPower(target) : 0
        };

        double spCost;
        try
        {
            spCost = SkillFormula.Evaluate(proto.SpCostPoly, vars);
        }
        catch (FormatException e)
        {
            _logger.LogError(e, "Failed to evaluate SP cost for {SkillId}: '{Formula}'", skillId, proto.SpCostPoly);
            return;
        }

        if (_player.Mana < spCost)
        {
            _player.SendChatInfo("Not enough SP.");
            return;
        }

        if (hasPrimaryEffect)
        {
            if (!isDamageSkill || canHitTarget)
            {
                ApplyEffect(proto.PointOn, proto.PointPoly, proto.DurationPoly, target, vars, skillId,
                    proto.AffectFlag, spCost, proto);
            }
            else if ((proto.Flags & ESkillFlags.SPLASH) == ESkillFlags.SPLASH && hasSecondaryEffect)
            {
                // Real mechanic (per the user's own live experience with the real game, since skilltable.txt
                // itself has no notion of "wait and see"): Dash-style charge skills don't just whiff their
                // damage half when nothing's in range at cast time - the MOV_SPEED buff granted below
                // "primes" a strike that fires automatically the moment a target comes within splash range
                // any time before the buff expires (walking into one, or one wandering/getting pulled into
                // range), without pressing the skill again. Checked every tick from
                // PlayerEntity.Update() -> TryTriggerPendingSplash(). "atk" is deliberately NOT reused from
                // `vars` here (it was computed against a null target, i.e. 0) - recomputed against whichever
                // real target is found when the strike actually lands.
                var duration = EvaluateDuration(proto.DurationPoly2, vars);
                _pendingSplash = (proto, vars, skillId, DateTime.UtcNow + duration);
            }
        }

        if (hasSecondaryEffect)
        {
            ApplyEffect(proto.PointOn2, proto.PointPoly2, proto.DurationPoly2, target, vars, skillId,
                proto.AffectFlag2, spCost, null);
        }

        _player.Mana = Math.Max(0, _player.Mana - (long)spCost);

        var cooldownSeconds = 1.0;
        try
        {
            cooldownSeconds = SkillFormula.Evaluate(proto.CooldownPoly, vars);
        }
        catch (FormatException e)
        {
            _logger.LogError(e, "Failed to evaluate cooldown for {SkillId}: '{Formula}'", skillId, proto.CooldownPoly);
        }

        _cooldowns[skillId] = DateTime.UtcNow + TimeSpan.FromSeconds(Math.Max(0.1, cooldownSeconds));

        _player.SendPoints();
    }

    private static bool IsNoneOrEmpty(string pointOn) =>
        string.IsNullOrWhiteSpace(pointOn) || string.Equals(pointOn, "NONE", StringComparison.OrdinalIgnoreCase);

    private void ApplyEffect(string pointOn, string pointPoly, string durationPoly, IEntity? target,
        Dictionary<string, double> vars, ESkill skillId, EAffectFlags affectFlag, double spCost,
        SkillData? splashContext)
    {
        if (IsNoneOrEmpty(pointOn))
        {
            return;
        }

        double raw;
        try
        {
            raw = SkillFormula.Evaluate(pointPoly, vars);
        }
        catch (FormatException e)
        {
            _logger.LogError(e, "Failed to evaluate point formula '{Formula}'", pointPoly);
            return;
        }

        switch (pointOn.ToUpperInvariant())
        {
            case "HP":
                if (target is null) return;
                // The formula's own value is already negative for a damaging effect (e.g.
                // "-(1.1*atk + ...)") - negate it back to a positive raw damage number, then apply the
                // same attack-vs-defence mitigation MeleeAttack uses, rather than the full rating-based
                // formula (which CalculateAttackPower already folded into "atk").
                var rawDamage = (int)Math.Round(-raw);
                var defence = (int)target.GetPoint(EPoint.DEFENCE_GRADE);
                var damage = Math.Max(1, rawDamage - defence);
                target.Damage(_player, EDamageType.NORMAL, damage);

                if (splashContext is { } proto && (proto.Flags & ESkillFlags.SPLASH) == ESkillFlags.SPLASH)
                {
                    ApplySplashDamage(proto, target, damage, vars);
                }

                break;
            case "ATT_SPEED":
                _player.ApplySkillBuff(EPoint.ATTACK_SPEED, raw, EvaluateDuration(durationPoly, vars), skillId,
                    affectFlag, spCost);
                break;
            case "ATT_GRADE":
                _player.ApplySkillBuff(EPoint.ATTACK_GRADE, raw, EvaluateDuration(durationPoly, vars), skillId,
                    affectFlag, spCost);
                break;
            case "DEF_GRADE":
                _player.ApplySkillBuff(EPoint.DEFENCE_GRADE, raw, EvaluateDuration(durationPoly, vars), skillId,
                    affectFlag, spCost);
                break;
            case "MOV_SPEED":
                // Always applied to self here (even for an ATTACK-flagged skill like Dash, which has no
                // SELF_ONLY flag) - read as "the caster closes the gap", not a target debuff; the formula's
                // positive value (a speed increase, not a slow) supports self over target too.
                _player.ApplySkillBuff(EPoint.MOVE_SPEED, raw, EvaluateDuration(durationPoly, vars), skillId,
                    affectFlag, spCost);
                break;
            case "CASTING_SPEED":
                _player.ApplySkillBuff(EPoint.CASTING_SPEED, raw, EvaluateDuration(durationPoly, vars), skillId,
                    affectFlag, spCost);
                break;
            default:
                _logger.LogDebug("Skill PointOn '{PointOn}' not modeled yet, ignoring its effect", pointOn);
                break;
        }
    }

    /// <summary>
    /// Hits every other alive monster within <see cref="SkillData.SplashRange"/> of the primary target
    /// (up to <see cref="SkillData.MaxHit"/> total, primary included) for the same damage, scaled by
    /// <see cref="SkillData.SplashAroundDamageAdjustPoly"/> (typically "1", i.e. full damage, in the real
    /// data actually seen so far).
    /// </summary>
    private void ApplySplashDamage(SkillData proto, IEntity primaryTarget, int primaryDamage,
        Dictionary<string, double> vars)
    {
        double adjust;
        try
        {
            adjust = SkillFormula.Evaluate(proto.SplashAroundDamageAdjustPoly, vars);
        }
        catch (FormatException e)
        {
            _logger.LogError(e, "Failed to evaluate splash damage adjust '{Formula}'",
                proto.SplashAroundDamageAdjustPoly);
            adjust = 1;
        }

        var splashDamage = Math.Max(1, (int)Math.Round(primaryDamage * adjust));
        var maxAdditionalHits = Math.Max(0, proto.MaxHit - 1);
        var hits = 0;

        foreach (var entity in _player.NearbyEntities)
        {
            if (hits >= maxAdditionalHits) break;
            if (entity is not MonsterEntity monster || monster.Dead || ReferenceEquals(entity, primaryTarget))
            {
                continue;
            }

            if (MathUtils.Distance(primaryTarget.PositionX, primaryTarget.PositionY, monster.PositionX,
                    monster.PositionY) > proto.SplashRange)
            {
                continue;
            }

            monster.Damage(_player, EDamageType.NORMAL, splashDamage);
            hits++;
        }
    }

    /// <summary>Checks (and, if ready, fires) a "primed strike" left behind by a whiffed Dash-style cast -
    /// see <see cref="_pendingSplash"/>'s own comment. Called every tick from
    /// <see cref="QuantumCore.Game.World.Entities.PlayerEntity.Update"/> so the hit lands the moment a
    /// target comes within range, not on some later re-cast. Cheap to call unconditionally: the common case
    /// (no pending strike) is a single null check.</summary>
    public void TryTriggerPendingSplash()
    {
        if (_pendingSplash is not { } pending) return;

        if (DateTime.UtcNow >= pending.Deadline)
        {
            _pendingSplash = null;
            return;
        }

        var target = FindNearestMonster(pending.Proto.SplashRange);
        if (target is null) return;

        _pendingSplash = null;
        pending.Vars["atk"] = _player.CalculateAttackPower(target);
        ApplyEffect(pending.Proto.PointOn, pending.Proto.PointPoly, pending.Proto.DurationPoly, target,
            pending.Vars, pending.SkillId, pending.Proto.AffectFlag, 0, pending.Proto);
        _player.SendPoints();
    }

    /// <summary>Closest alive monster within range of the caster - the fallback target for a SPLASH-
    /// flagged skill cast without an explicit unit locked (see Use()'s own comment on why that happens).
    /// </summary>
    private IEntity? FindNearestMonster(double range)
    {
        IEntity? closest = null;
        var closestDistance = range;

        foreach (var entity in _player.NearbyEntities)
        {
            if (entity is not MonsterEntity monster || monster.Dead) continue;

            var distance = MathUtils.Distance(_player.PositionX, _player.PositionY, monster.PositionX,
                monster.PositionY);
            if (distance > closestDistance) continue;

            closest = monster;
            closestDistance = distance;
        }

        return closest;
    }

    private TimeSpan EvaluateDuration(string durationPoly, Dictionary<string, double> vars)
    {
        try
        {
            return TimeSpan.FromSeconds(Math.Max(0, SkillFormula.Evaluate(durationPoly, vars)));
        }
        catch (FormatException e)
        {
            _logger.LogError(e, "Failed to evaluate duration formula '{Formula}'", durationPoly);
            return TimeSpan.Zero;
        }
    }

    private void SendSkillLevelsPacket()
    {
        var levels = new SkillLevels();
        for (var i = 0; i < SKILL_MAX_NUM; i++)
        {
            levels.Skills[i] = new PlayerSkill
            {
                Level = ESkillLevel.UNLEARNED, MasterType = ESkillMasterType.NORMAL, NextReadTime = 0
            };
        }

        for (var i = 0; i < _skills.Count; i++)
        {
            var skill = _skills.ElementAt(i);

            levels.Skills[(byte)skill.Key] = new PlayerSkill
            {
                Level = skill.Value.Level,
                MasterType = skill.Value.MasterType,
                NextReadTime = skill.Value.NextReadTime
            };
        }

        _player.Connection.Send(levels);
    }

    /// <summary>
    /// Whether the player has actually picked a skill branch (BRANCH_A/BRANCH_B) yet.
    /// <see cref="ESkillGroup.UNKNOWN"/> - the value every player starts with before choosing - is
    /// itself a valid, defined enum member, so <c>SkillGroup.IsDefined()</c> alone is NOT enough to
    /// tell "no group chosen" apart from "a real group chosen"; it must be excluded explicitly.
    /// </summary>
    private bool HasChosenSkillGroup() =>
        _player.Player.SkillGroup.IsDefined() && _player.Player.SkillGroup != ESkillGroup.UNKNOWN;

    private void AssignDefaultActiveSkills()
    {
        for (var i = 0; i < SKILL_COUNT; i++)
        {
            var skill = SkillList[(int)_player.Player.PlayerClass.GetClass(), (byte)_player.Player.SkillGroup - 1, i];
            if (skill == 0) continue;

            _skills[skill] = new Skill
            {
                Level = ESkillLevel.UNLEARNED,
                MasterType = ESkillMasterType.NORMAL,
                NextReadTime = 0,
                SkillId = skill,
                PlayerId = _player.Player.Id,
            };
        }
    }

    private void AssignDefaultPassiveSkills()
    {
        foreach (var skill in PassiveSkillIds)
        {
            _skills[skill] = new Skill
            {
                Level = ESkillLevel.UNLEARNED,
                MasterType = ESkillMasterType.NORMAL,
                NextReadTime = 0,
                SkillId = skill,
                PlayerId = _player.Player.Id,
            };
        }
    }
}