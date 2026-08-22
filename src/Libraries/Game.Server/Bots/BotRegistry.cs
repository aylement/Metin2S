using QuantumCore.API.Game.World;

namespace QuantumCore.Game.Bots;

/// <summary>
/// Keeps a reference to every internal bot created via <see cref="IPlayerFactory"/> so they are not
/// garbage collected once the creating command/service returns, and so later steps (spawning into the
/// world, listing, despawning) can find them again by name.
/// </summary>
public class BotRegistry
{
    private readonly Dictionary<string, IPlayerEntity> _bots = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyCollection<IPlayerEntity> Bots => _bots.Values;

    public void Add(IPlayerEntity bot)
    {
        ArgumentNullException.ThrowIfNull(bot);
        _bots[bot.Name] = bot;
    }

    public bool Contains(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return _bots.ContainsKey(name);
    }

    public IPlayerEntity? Get(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return _bots.GetValueOrDefault(name);
    }

    public void Remove(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        _bots.Remove(name);
    }
}
