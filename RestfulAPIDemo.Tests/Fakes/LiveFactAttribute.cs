namespace RestfulAPIDemo.Tests.Fakes;

/// <summary>A fact that only runs when <c>HN_LIVE_TESTS=1</c>, because it talks to the real Hacker News API.</summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class LiveFactAttribute : FactAttribute
{
    public LiveFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("HN_LIVE_TESTS") != "1")
        {
            Skip = "Set HN_LIVE_TESTS=1 to run tests against the real Hacker News API.";
        }
    }
}
