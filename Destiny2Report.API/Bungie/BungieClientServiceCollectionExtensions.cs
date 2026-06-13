using D2Report.BungieClient;
using D2Report.BungieClient.RateLimiting;
using Microsoft.Extensions.Options;

namespace Destiny2Report.API.Bungie;

public static class BungieClientServiceCollectionExtensions
{
    public static IServiceCollection AddBungieClient(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<BungieClientOptions>(configuration.GetSection(BungieClientOptions.SectionName));

        var rateLimiterOptions = new BungieClientRateLimitOptions();

        rateLimiterOptions.SetEndpointLimit("GET Platform/Destiny2/Stats/PostGameCarnageReport/{value}", 50);

        services.AddSingleton(rateLimiterOptions);
        services.AddTransient<BungieClientRetryHandler>();
        services.AddTransient<BungieClientRateLimitingHandler>();

        services.AddHttpClient<ID2ReportClient, D2ReportClient>((serviceProvider, httpClient) =>
            {
                var options = serviceProvider.GetRequiredService<IOptions<BungieClientOptions>>().Value;

                if (!string.IsNullOrWhiteSpace(options.ApiKey))
                {
                    httpClient.DefaultRequestHeaders.Add("X-API-Key", options.ApiKey);
                }
            })
            .ConfigurePrimaryHttpMessageHandler(BungieClientHandlers.CreateRedirectHandler)
            .AddHttpMessageHandler<BungieClientRetryHandler>()
            .AddHttpMessageHandler<BungieClientRateLimitingHandler>();

        return services;
    }
}
