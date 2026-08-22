using QuantumCore.API.Game.Types.Skills;

namespace QuantumCore.API.Game.Skills;

public interface IPlayerSkills
{
    Task LoadAsync();
    Task PersistAsync();

    ISkill? this[ESkill skillId] { get; }

    void SetSkillGroup(ESkillGroup skillGroup);
    void ClearSkills();
    void ClearSubSkills();
    void Reset(ESkill skillId);
    void SetLevel(ESkill skillId, ESkillLevel level);
    void SkillUp(ESkill skillId, ESkillLevelMethod method = ESkillLevelMethod.POINT);
    bool CanUse(ESkill skillId);

    /// <summary>
    /// Send skill info to client
    /// </summary>
    void Send();

    bool LearnSkillByBook(ESkill skillId);
    void SetSkillNextReadTime(ESkill skillId, int time);

    /// <summary>Casts a learned active skill against the given target (0 for a self-only skill) - checks
    /// it's known, off cooldown, and affordable, then applies its real skilltable.txt effect.</summary>
    void Use(ESkill skillId, uint targetVid);

    /// <summary>Every active skill this player has actually learned (level above UNLEARNED) that has a
    /// real, castable skilltable.txt effect - i.e. skills <see cref="Use"/> could plausibly succeed on
    /// right now, cooldown/SP aside. Used by bots to pick something to cast during combat without needing
    /// their own copy of the class/branch skill table.</summary>
    IEnumerable<ESkill> GetUsableActiveSkillIds();

    /// <summary>Checks (and fires, if ready) a "primed strike" left behind by a Dash-style skill cast with
    /// nothing in range to hit - see the implementation's own doc comment for the real mechanic this
    /// models. Call every tick; cheap no-op when nothing is pending.</summary>
    void TryTriggerPendingSplash();
}
