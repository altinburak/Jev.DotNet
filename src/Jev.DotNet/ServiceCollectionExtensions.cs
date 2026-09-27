using Jev.DotNet;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

// Lives in the DI namespace so `services.AddJevClient()` is discoverable without an extra using.
namespace Microsoft.Extensions.DependencyInjection;

/// <summary>Registers <see cref="IJevClient"/> with dependency injection.</summary>
public static class JevServiceCollectionExtensions
{
    /// <summary>
    /// Register <see cref="IJevClient"/> as a typed client backed by <c>IHttpClientFactory</c>.
    /// Returns the <see cref="IHttpClientBuilder"/> so you can add handlers, proxies, or HTTP/2.
    /// </summary>
    /// <example>
    /// <code>
    /// builder.Services.AddJevClient(o => o.ApiKey = builder.Configuration["TypeSafe:ApiKey"]);
    /// // or bind a section: builder.Services.AddJevClient(builder.Configuration.GetSection("TypeSafe").Bind);
    /// </code>
    /// </example>
    public static IHttpClientBuilder AddJevClient(this IServiceCollection services, Action<JevClientOptions>? configure = null)
    {
        var optionsBuilder = services.AddOptions<JevClientOptions>();
        if (configure is not null) optionsBuilder.Configure(configure);

        return services
            .AddHttpClient<IJevClient, JevClient>((http, sp) => new JevClient(
                sp.GetRequiredService<IOptions<JevClientOptions>>().Value,
                http,
                sp.GetService<ILoggerFactory>()?.CreateLogger<JevClient>()))
            // JevClient enforces its own per-attempt timeout; don't let HttpClient's 100 s default cut retries short.
            .ConfigureHttpClient(http => http.Timeout = Timeout.InfiniteTimeSpan);
    }
}
