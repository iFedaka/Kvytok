namespace Kvytok.Worker;

/// <summary>Bound from the "Retry" section: how a message whose handling failed is tried again.</summary>
public sealed class RetryOptions
{
    public const string Section = "Retry";

    /// <summary>Tries in all, the first delivery included; the last failure goes to the dead-letter queue.</summary>
    public int MaxAttempts { get; set; } = 5;

    /// <summary>The wait after the first failure; it doubles after each one after that.</summary>
    public TimeSpan FirstDelay { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>The longest wait between two tries, whatever the doubling reaches.</summary>
    public TimeSpan MaxDelay { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>
    /// The wait after <paramref name="failedAttempt"/> (1 for the first delivery), spread by ±20% so
    /// messages that failed together, say in a database outage, do not all come back together.
    /// </summary>
    public TimeSpan DelayAfter(int failedAttempt, Random random)
    {
        var doubled = FirstDelay * Math.Pow(2, Math.Min(failedAttempt - 1, 30));
        var capped = doubled < MaxDelay ? doubled : MaxDelay;
        return capped * (0.8 + 0.4 * random.NextDouble());
    }
}
