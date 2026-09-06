using Microsoft.Extensions.Time.Testing;

namespace RestfulAPIDemo.Tests.Fakes;

public static class FakeTimeExtensions
{
    /// <summary>
    /// Advances the fake clock in steps, yielding to the thread pool between steps, until <paramref name="done"/>
    /// returns <see langword="true"/>. Sidesteps the race between a timer being registered and time being advanced.
    /// </summary>
    public static async Task AdvanceUntilAsync(this FakeTimeProvider time, Func<bool> done, TimeSpan step, int maxSteps = 100)
    {
        for (var i = 0; i < maxSteps && !done(); i++)
        {
            time.Advance(step);
            for (var yields = 0; yields < 20 && !done(); yields++)
            {
                await Task.Delay(5);
            }
        }

        if (!done())
        {
            throw new TimeoutException($"Condition not met after advancing {step} x {maxSteps}.");
        }
    }

    /// <summary>Polls (real clock) until <paramref name="done"/> returns <see langword="true"/>.</summary>
    public static async Task WaitUntilAsync(Func<bool> done, TimeSpan? timeout = null, string? what = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(10));
        while (!done())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"Timed out waiting for {what ?? "condition"}.");
            }

            await Task.Delay(5);
        }
    }
}
