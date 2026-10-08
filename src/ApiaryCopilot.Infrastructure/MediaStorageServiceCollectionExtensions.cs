using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace ApiaryCopilot.Infrastructure;

/// <summary>Registers the media bucket.</summary>
public static class MediaStorageServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="IMediaStorage"/> over the S3-compatible bucket and validates <see cref="MediaOptions"/> when
    /// the host starts, so a malformed setting stops it before it serves a request. Returns the options' builder for
    /// the host to bind them to its configuration.
    /// </summary>
    public static OptionsBuilder<MediaOptions> AddMediaStorage(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<MediaOptions>, MediaOptionsValidator>());
        services.AddSingleton<IMediaStorage, S3MediaStorage>();
        return services.AddOptions<MediaOptions>().ValidateOnStart();
    }
}
