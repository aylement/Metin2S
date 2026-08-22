using Microsoft.Extensions.DependencyInjection;
using QuantumCore.API;
using QuantumCore.API.Core.Models;
using QuantumCore.API.Game.World;

namespace QuantumCore.Game.Bots;

/// <summary>
/// Mirrors <see cref="QuantumCore.Game.PlayerFactory"/> exactly (same ActivatorUtilities.CreateInstance +
/// LoadAsync() pattern) but produces a <see cref="BotPlayerEntity"/> instead of a plain
/// <see cref="QuantumCore.Game.World.Entities.PlayerEntity"/>, so bots get their own wander behaviour.
/// Kept separate from <see cref="IPlayerFactory"/> on purpose: that one is shared production code for
/// real players and shouldn't need to know about bot-only entity types.
/// </summary>
public class BotPlayerFactory
{
    private readonly IServiceProvider _provider;

    public BotPlayerFactory(IServiceProvider provider)
    {
        _provider = provider;
    }

    public async Task<IPlayerEntity> CreateBotAsync(IGameConnection connection, PlayerData player)
    {
        var entity = ActivatorUtilities.CreateInstance<BotPlayerEntity>(_provider, [connection, player]);
        await entity.LoadAsync();

        return entity;
    }
}
