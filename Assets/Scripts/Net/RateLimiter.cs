using System;
using System.Collections.Generic;

/// <summary>Token-bucket rate limiting per (connection, command).</summary>
public sealed class RateLimiter
{
    private readonly Dictionary<string, (float capacity, float refill)> rules;
    private readonly (float capacity, float refill) defaultRule;
    private readonly Dictionary<(int, string), (float tokens, double last)> buckets = new Dictionary<(int, string), (float, double)>();

    public RateLimiter(IDictionary<string, (float capacity, float refillPerSecond)> rules, (float capacity, float refillPerSecond) defaultRule)
    {
        this.rules = new Dictionary<string, (float, float)>();
        foreach (KeyValuePair<string, (float, float)> rule in rules) this.rules[rule.Key] = rule.Value;
        this.defaultRule = defaultRule;
    }

    public bool TryConsume(int connectionId, string command, double now)
    {
        (float capacity, float refill) rule = rules.TryGetValue(command, out var r) ? r : defaultRule;
        var key = (connectionId, command);
        if (!buckets.TryGetValue(key, out var bucket)) bucket = (rule.capacity, now);

        double elapsed = Math.Max(0.0, now - bucket.last);
        float tokens = (float)Math.Min(rule.capacity, bucket.tokens + elapsed * rule.refill);
        if (tokens < 1f)
        {
            buckets[key] = (tokens, now);
            return false;
        }
        buckets[key] = (tokens - 1f, now);
        return true;
    }

    public void Forget(int connectionId)
    {
        var stale = new List<(int, string)>();
        foreach ((int, string) key in buckets.Keys) if (key.Item1 == connectionId) stale.Add(key);
        foreach ((int, string) key in stale) buckets.Remove(key);
    }
}
