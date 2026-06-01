namespace AdGroups2Cmdbuild.Resilience;

public static class RetryBackoff
{
    public static TimeSpan CalculateDelay(int attempt, int baseDelayMs, int maxDelayMs, int jitterPercent)
    {
        var safeBaseDelayMs = Math.Max(1, baseDelayMs);
        var safeMaxDelayMs = Math.Max(safeBaseDelayMs, maxDelayMs);
        var multiplier = Math.Pow(2, Math.Max(0, attempt - 1));
        var delayMs = Math.Min(safeMaxDelayMs, safeBaseDelayMs * multiplier);
        var safeJitterPercent = Math.Clamp(jitterPercent, 0, 100);

        if (safeJitterPercent > 0)
        {
            var jitterRange = delayMs * safeJitterPercent / 100d;
            var jitter = (Random.Shared.NextDouble() * 2d - 1d) * jitterRange;
            delayMs = Math.Min(safeMaxDelayMs, Math.Max(1, delayMs + jitter));
        }

        return TimeSpan.FromMilliseconds(delayMs);
    }
}
