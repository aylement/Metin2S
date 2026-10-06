using System.Collections;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using QuantumCore.API;

namespace QuantumCore.Caching.InMemory;

/// <summary>
/// Basic implementation of a in-memory equivalent of redis
/// </summary>
public class InMemoryRedisStore : IRedisStore
{
    private readonly Dictionary<string, (object Value, DateTime? Expiry)> _dict = new();
    private readonly List<InMemoryRedisSubscriber> _subscribers = [];

    public IRedisListWrapper<T> CreateList<T>(string name)
    {
        if (_dict.TryGetValue(name, out var value))
        {
            if (value.Expiry is not null && value.Expiry < DateTime.UtcNow)
            {
                _dict.Remove(name);
            }
            else
            {
                Debug.Assert(value.Value.GetType().IsAssignableTo(typeof(IRedisListWrapper<T>)), "type mismatch");
                return (IRedisListWrapper<T>)value.Value;
            }
        }

        var list = new InMemoryRedisListWrapper<T>();
        _dict.Add(name, (list, null));
        return list;
    }

    public ValueTask<long> DelAsync(string key)
    {
        _dict.Remove(key);
        return ValueTask.FromResult(1L);
    }

    public ValueTask<string> SetAsync(string key, object item)
    {
        _dict[key] = (item, null);
        return ValueTask.FromResult(key);
    }

    public ValueTask<T> GetAsync<T>(string key)
    {
        if (_dict.TryGetValue(key, out var value))
        {
            if (value.Expiry is not null && value.Expiry < DateTime.UtcNow)
            {
                _dict.Remove(key);
                return default;
            }

            return ValueTask.FromResult((T)value.Value);
        }

        return default;
    }

    public async ValueTask<long> ExistsAsync(string key)
    {
        if (_dict.TryGetValue(key, out var value))
        {
            if (value.Expiry is not null && value.Expiry < DateTime.UtcNow)
            {
                _dict.Remove(key);
                return 0;
            }

            if (value.Value is IRedisListWrapper enumerable && await enumerable.LenAsync() == 0)
            {
                // empty list equals no result
                return 0;
            }

            return 1;
        }

        return 0;
    }

    public ValueTask<long> ExpireAsync(string key, TimeSpan seconds)
    {
        if (_dict.TryGetValue(key, out var tuple))
        {
            tuple.Expiry = DateTime.UtcNow + seconds;
            _dict[key] = tuple;
        }

        return ValueTask.FromResult(1L);
    }

    public ValueTask<bool> PingAsync()
    {
        return ValueTask.FromResult(true);
    }

    public ValueTask<long> PublishAsync(string key, object obj)
    {
        ArgumentNullException.ThrowIfNull(obj);
        var callbacks = _subscribers
            .SelectMany(x => x.Callbacks)
            .Where(x => x.Key == key)
            .Select(x => x.Value)
            .ToArray();
        foreach (var callback in callbacks)
        {
            var actionType = typeof(Action<>).MakeGenericType(obj.GetType());
            var methodInfo = actionType.GetMethod(nameof(Action<>.Invoke))!;

            foreach (var action in (IEnumerable)callback)
            {
                methodInfo.Invoke(action, [obj]);
            }
        }

        return ValueTask.FromResult((long)callbacks.Length);
    }

    public IRedisSubscriber Subscribe()
    {
        var sub = new InMemoryRedisSubscriber();
        _subscribers.Add(sub);
        return sub;
    }

    public ValueTask<string[]> KeysAsync(string key)
    {
        var regex = RedisPatternToRegex(key);
        var matchedKeys = _dict.Keys.Where(x => regex.IsMatch(x)).ToArray();
        return ValueTask.FromResult(matchedKeys);
    }

    public ValueTask<long> PersistAsync(string key)
    {
        // Mirrors ExpireAsync's pattern in reverse: clear the TTL so the key survives indefinitely,
        // like real Redis's PERSIST command. This was a no-op stub before, which silently broke
        // TokenLoginHandler's "remove TTL so the auth token survives further game core transitions"
        // step (see its own comment) for anyone running the in-memory store (Single/dev mode) - the
        // token kept its original 30-second TTL from LoginRequestHandler, so any warp chain taking
        // longer than 30s total (e.g. multi-hop map transitions like Devil Tower's access map relay)
        // hit "Received invalid auth token" and got disconnected. Confirmed live: deviltower1 ->
        // milgyo -> deviltower1's actual entrance, ~45s total, failed on the 3rd hop.
        if (_dict.TryGetValue(key, out var tuple) && tuple.Expiry is not null)
        {
            tuple.Expiry = null;
            _dict[key] = tuple;
            return ValueTask.FromResult(1L);
        }

        return ValueTask.FromResult(0L);
    }

    public ValueTask<string> FlushAllAsync()
    {
        _dict.Clear();

        return ValueTask.FromResult("");
    }

    public ValueTask DelAllAsync(string pattern)
    {
        var regex = RedisPatternToRegex(pattern);

        var keys = _dict.Keys.Where(x => regex.IsMatch(x)).ToArray();
        foreach (var key in keys)
        {
            _dict.Remove(key);
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask<long> IncrAsync(string key)
    {
        if (!_dict.TryGetValue(key, out var tuple) || tuple.Expiry is not null && tuple.Expiry < DateTime.UtcNow)
        {
            // according to redis docs the value is set to 0 if it does not exist before incrementing
            tuple = (0, null);
        }

        _dict[key] = ((int)tuple.Value + 1, tuple.Expiry);
        return ValueTask.FromResult(Convert.ToInt64(_dict[key].Value));
    }

    /// <summary>
    /// replace * (redis match all) with regex .*
    /// </summary>
    public static Regex RedisPatternToRegex(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        var regexPattern = new StringBuilder();
        var currentIndex = 0;
        int asterixIndex;
        while ((asterixIndex = key.IndexOf('*', currentIndex)) >= 0)
        {
            regexPattern.Append(Regex.Escape($"{key[currentIndex..asterixIndex]}"));
            regexPattern.Append(".*");
            currentIndex = asterixIndex + 1;
        }

        if (currentIndex < key.Length - 1)
        {
            regexPattern.Append(Regex.Escape($"{key[currentIndex..]}"));
        }

        return new Regex(regexPattern.ToString());
    }
}