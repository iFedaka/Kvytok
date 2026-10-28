namespace Kvytok.Worker.IntegrationTests;

public sealed class RetryOptionsTests
{
    private readonly RetryOptions _options = new()
    {
        FirstDelay = TimeSpan.FromSeconds(2),
        MaxDelay = TimeSpan.FromSeconds(10),
    };

    [Theory]
    [InlineData(1, 2)]
    [InlineData(2, 4)]
    [InlineData(3, 8)]
    [InlineData(4, 10)]
    [InlineData(40, 10)]
    public void Doubles_Up_To_The_Cap_Within_The_Jitter(int failedAttempt, double seconds)
    {
        var random = new Random(failedAttempt);
        for (var i = 0; i < 100; i++)
        {
            var delay = _options.DelayAfter(failedAttempt, random);
            Assert.InRange(delay.TotalSeconds, seconds * 0.8, seconds * 1.2);
        }
    }
}
