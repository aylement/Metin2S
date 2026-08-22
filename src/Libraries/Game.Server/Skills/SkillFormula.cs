using System.Globalization;
using QuantumCore.Core.Utils;

namespace QuantumCore.Game.Skills;

/// <summary>
/// Evaluates the small arithmetic expressions used throughout skilltable.txt's <c>PointPoly</c>/
/// <c>SpCostPoly</c>/<c>CooldownPoly</c>/<c>DurationPoly</c> columns - e.g.
/// <c>-( 1.1*atk + (0.5*atk + 1.5 * str)*k)</c> or <c>40+100*k</c>. The original server never evaluates
/// these live: it precomputes them offline into per-level lookup tables (<c>skill_power.cpp</c>'s
/// <c>CTableBySkill</c>) that aren't part of the source available here, so this evaluates the formula text
/// directly at cast time instead - same real formulas, computed live rather than pre-baked. Supports
/// +, -, *, /, unary minus, parentheses, and the two functions the table actually uses:
/// <c>floor(x)</c> and <c>number(min, max)</c> (inclusive random integer, matching the original
/// script language's own <c>number()</c>).
/// </summary>
public static class SkillFormula
{
    public static double Evaluate(string expression, IReadOnlyDictionary<string, double> variables)
    {
        ArgumentNullException.ThrowIfNull(expression);
        ArgumentNullException.ThrowIfNull(variables);

        if (string.IsNullOrWhiteSpace(expression))
        {
            return 0;
        }

        var parser = new Parser(expression, variables);
        var result = parser.ParseExpression();
        parser.SkipWhitespace();
        if (!parser.AtEnd)
        {
            throw new FormatException($"Unexpected trailing input in skill formula '{expression}' at position {parser.Position}");
        }

        return result;
    }

    private sealed class Parser
    {
        private readonly string _s;
        private readonly IReadOnlyDictionary<string, double> _vars;
        public int Position;

        public Parser(string s, IReadOnlyDictionary<string, double> vars)
        {
            _s = s;
            _vars = vars;
        }

        public bool AtEnd => Position >= _s.Length;

        public void SkipWhitespace()
        {
            while (!AtEnd && char.IsWhiteSpace(_s[Position])) Position++;
        }

        private char Peek()
        {
            SkipWhitespace();
            return AtEnd ? '\0' : _s[Position];
        }

        private bool Consume(char c)
        {
            if (Peek() != c) return false;
            Position++;
            return true;
        }

        // expr := term (('+' | '-') term)*
        public double ParseExpression()
        {
            var value = ParseTerm();
            while (true)
            {
                if (Consume('+')) value += ParseTerm();
                else if (Consume('-')) value -= ParseTerm();
                else break;
            }

            return value;
        }

        // term := unary (('*' | '/') unary)*
        private double ParseTerm()
        {
            var value = ParseUnary();
            while (true)
            {
                if (Consume('*')) value *= ParseUnary();
                else if (Consume('/')) value /= ParseUnary();
                else break;
            }

            return value;
        }

        // unary := '-' unary | primary
        private double ParseUnary()
        {
            if (Consume('-')) return -ParseUnary();
            Consume('+');
            return ParsePrimary();
        }

        // primary := NUMBER | IDENT '(' args ')' | IDENT | '(' expr ')'
        private double ParsePrimary()
        {
            var c = Peek();

            if (c == '(')
            {
                Position++;
                var value = ParseExpression();
                if (!Consume(')'))
                {
                    throw new FormatException($"Expected ')' at position {Position} in '{_s}'");
                }

                return value;
            }

            if (char.IsDigit(c) || c == '.')
            {
                return ParseNumber();
            }

            if (char.IsLetter(c) || c == '_')
            {
                return ParseIdentifierOrCall();
            }

            throw new FormatException($"Unexpected character '{c}' at position {Position} in '{_s}'");
        }

        private double ParseNumber()
        {
            SkipWhitespace();
            var start = Position;
            while (!AtEnd && (char.IsDigit(_s[Position]) || _s[Position] == '.')) Position++;
            return double.Parse(_s.AsSpan(start, Position - start), CultureInfo.InvariantCulture);
        }

        private double ParseIdentifierOrCall()
        {
            SkipWhitespace();
            var start = Position;
            while (!AtEnd && (char.IsLetterOrDigit(_s[Position]) || _s[Position] == '_')) Position++;
            var name = _s[start..Position];

            if (Peek() == '(')
            {
                Position++;
                var args = new List<double> { ParseExpression() };
                while (Consume(',')) args.Add(ParseExpression());
                if (!Consume(')'))
                {
                    throw new FormatException($"Expected ')' after arguments to '{name}' in '{_s}'");
                }

                return CallFunction(name, args);
            }

            if (_vars.TryGetValue(name, out var value)) return value;

            // Unknown variable (e.g. a formula referencing something this evaluator doesn't model yet,
            // like "maxhp" or "ar") - treat as 0 rather than throwing, so a skill still casts with a
            // reasonable (if not perfectly authentic) result instead of failing outright.
            return 0;
        }

        private static double CallFunction(string name, List<double> args)
        {
            switch (name.ToUpperInvariant())
            {
                case "FLOOR":
                    return Math.Floor(args[0]);
                case "NUMBER":
                    // Inclusive random integer in [min, max], matching the original script language's
                    // own number(min, max).
                    var min = (int)args[0];
                    var max = (int)args[1];
                    return CoreRandom.GenerateInt32(min, max + 1);
                default:
                    throw new FormatException($"Unknown function '{name}' in skill formula");
            }
        }
    }
}
