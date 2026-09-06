namespace RestfulAPIDemo.BestStories;

public static class BestStoriesServiceCollectionExtensions
{
    /// <summary>Registers the snapshot store, the background refresher and the readiness health check.</summary>
    public static IServiceCollection AddBestStories(this IServiceCollection services)
    {
        services.AddOptions<BestStoriesOptions>()
            .BindConfiguration(BestStoriesOptions.SectionName)
            .ValidateDataAnnotations()
            .Validate(o => o.RefreshTimeout < o.RefreshInterval, "BestStories:RefreshTimeout must be less than BestStories:RefreshInterval so cycles never overlap.")
            .ValidateOnStart();

        services.AddSingleton<SnapshotStore>();

        // Registered as a singleton *and* as the hosted service so tests can resolve it and drive cycles directly.
        services.AddSingleton<BestStoriesRefresher>();
        services.AddHostedService(serviceProvider => serviceProvider.GetRequiredService<BestStoriesRefresher>());

        services.AddHealthChecks().AddCheck<SnapshotHealthCheck>("best-stories-snapshot", tags: ["ready"]);
        return services;
    }
}
