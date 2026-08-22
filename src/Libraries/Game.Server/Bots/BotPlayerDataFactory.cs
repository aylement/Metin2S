using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using QuantumCore.API;
using QuantumCore.API.Core.Models;
using QuantumCore.API.Game.Types;
using QuantumCore.API.Game.Types.Players;
using QuantumCore.API.Game.Types.Skills;
using QuantumCore.Game.Extensions;

namespace QuantumCore.Game.Bots;

/// <summary>
/// Builds an in-memory <see cref="PlayerData"/> for an internal bot - no database row is read or written here.
/// </summary>
public class BotPlayerDataFactory
{
    /// <summary>
    /// Reserved, fixed AccountId used for every bot. Intentionally NOT <see cref="Guid.Empty"/>:
    /// <see cref="QuantumCore.Game.Persistence.DbPlayerRepository.GetPlayerAsync(uint)"/> treats a
    /// looked-up AccountId of <see cref="Guid.Empty"/> as "player not found" (it's the default value
    /// EF Core returns for a missing row), so using it here would make a persisted bot silently
    /// unfindable by id. This value does not need to reference a real account: nothing in
    /// GameDbContext (see src/Data/Game.Persistence/Entities/Player.cs) declares a foreign key from
    /// Player.AccountId to an account table, so the database will accept it as-is if a bot is ever
    /// persisted.
    /// </summary>
    public static readonly Guid BotAccountId = Guid.Parse("00000000-0000-0000-0000-0000000b0745");

    private readonly IJobManager _jobManager;
    private readonly GameOptions _gameOptions;
    private readonly ILogger<BotPlayerDataFactory> _logger;

    public BotPlayerDataFactory(IJobManager jobManager, IOptions<GameOptions> gameOptions,
        ILogger<BotPlayerDataFactory> logger)
    {
        ArgumentNullException.ThrowIfNull(jobManager);
        ArgumentNullException.ThrowIfNull(gameOptions);
        _jobManager = jobManager;
        _gameOptions = gameOptions.Value;
        _logger = logger;
    }

    /// <summary>
    /// Creates a fresh, never-persisted <see cref="PlayerData"/> for a bot.
    /// The caller is responsible for picking a <paramref name="name"/> that is not already in use
    /// (e.g. via <see cref="QuantumCore.API.Game.World.IWorld.GetPlayer"/>) - this factory does not
    /// check the database nor the live world for name collisions. The caller is also expected to set
    /// PositionX/PositionY afterwards if the empire's default town spawn (or the fallback below) isn't
    /// where the bot should appear - <see cref="QuantumCore.Game.Commands.SpawnBotCommand"/> always does.
    /// </summary>
    /// <param name="name">Unique character name for the bot.</param>
    /// <param name="empire">Empire the bot belongs to - determines its default spawn position.</param>
    /// <param name="playerClass">Gendered class of the bot, must be a valid, configured job.</param>
    public PlayerData Create(string name, EEmpire empire, EPlayerClassGendered playerClass)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        var job = _jobManager.Get(playerClass);
        if (job is null)
        {
            throw new InvalidOperationException($"No job data found for class {playerClass}");
        }

        // Unlike the job lookup above, a missing empire spawn position is not fatal: callers that
        // override PositionX/PositionY afterwards (e.g. to spawn the bot next to a GM) don't need it
        // configured at all, and "Game:Empire" is not set in this repo's shipped appsettings.json.
        if (!_gameOptions.Empire.TryGetValue(empire, out var spawnPosition))
        {
            _logger.LogWarning(
                "No spawn position configured for empire {Empire} (see 'Game:Empire' configuration) - " +
                "defaulting bot '{Name}' to (0;0)", empire, name);
            spawnPosition = new Coordinates(0, 0);
        }

        return new PlayerData
        {
            Id = 0, // never fetched/loaded by id - this bot is not backed by a database row
            AccountId = BotAccountId,
            Name = name,
            PlayerClass = playerClass,
            SkillGroup = ESkillGroup.BRANCH_A, // a real player only gets a group once they pick one in-game;
                                                // give bots one upfront so PlayerSkills.LoadAsync() has active skills to assign
            Empire = empire,
            // Not level 1: BotPlayerEntity now masters every active skill it has (see
            // BotPlayerEntity.MasterAllUsableSkills), and this repo's own real skilltable.txt SP-cost
            // formulas scale with skill mastery (k), not character level - a level-1 SP pool couldn't
            // afford a single cast of a fully-mastered skill. PlayerEntity.LoadAsync() recomputes
            // MaxHp/MaxSp/Health/Mana from this Level via IJobManager, so bumping it here alone is enough
            // to give bots a real SP pool to actually use their skills with.
            Level = 90,
            PositionX = (int)spawnPosition.X,
            PositionY = (int)spawnPosition.Y,
            St = job.St,
            Ht = job.Ht,
            Dx = job.Dx,
            Iq = job.Iq,
            Health = job.StartHp,
            Mana = job.StartSp,
            Slot = 0,
            HorseLevel = HorseVnums.MaxLevel
            // MaxHp/MaxSp are intentionally left at their default (0): PlayerEntity.LoadAsync()
            // computes them from Level/Ht/Iq via IJobManager, same as for a real player.
        };
    }
}
