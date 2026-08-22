namespace QuantumCore.Core.Utils;

public static class EnumUtils
{
    /// <summary>
    /// Parses a data-file token (e.g. from skilltable.txt's flag columns) against an enum, tolerating a
    /// mismatch in underscore placement between the two: some data-file tokens omit underscores a C#
    /// enum member name has (e.g. the real skilltable.txt's "SELFONLY" vs. <c>ESkillFlags.SELF_ONLY</c>).
    /// Compares both sides with underscores stripped, case-insensitively, rather than assuming the
    /// caller's underscore usage exactly matches the enum's - the previous implementation special-cased
    /// "does the input contain an underscore" and reformatted only that side, which silently failed to
    /// match any token whose underscore usage didn't already agree with its enum member (confirmed via
    /// "SELFONLY": found while wiring skill execution up to these flags for the first time - nothing
    /// consumed <see cref="QuantumCore.API.Game.Types.Skills.ESkillFlags"/> before, so this always failed
    /// silently without visibly breaking anything).
    /// </summary>
    public static bool TryParseEnum<TEnum>(string value, out TEnum result) where TEnum : struct, Enum
    {
        ArgumentNullException.ThrowIfNull(value);

        var normalizedValue = value.Replace("_", "");

        foreach (var name in Enum.GetNames<TEnum>())
        {
            if (string.Equals(name.Replace("_", ""), normalizedValue, StringComparison.OrdinalIgnoreCase))
            {
                result = Enum.Parse<TEnum>(name);
                return true;
            }
        }

        result = default;
        return false;
    }
}