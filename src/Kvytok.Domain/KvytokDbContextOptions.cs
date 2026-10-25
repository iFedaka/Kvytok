using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace Kvytok.Domain;

public static class KvytokDbContextOptions
{
    /// <summary>
    /// The interceptors every <see cref="KvytokDbContext"/> needs. Added at each registration because
    /// DbContext pooling forbids adding them in OnConfiguring; the context refuses to start without them.
    /// </summary>
    public static TBuilder AddKvytokInterceptors<TBuilder>(this TBuilder options) where TBuilder : DbContextOptionsBuilder =>
        (TBuilder)options.AddInterceptors(OutboxInterceptor.Instance);

    /// <summary>
    /// Throws unless <paramref name="options"/> carry the outbox interceptor. Without it a save would
    /// commit the change and drop its messages with no error, so a missed registration fails at once.
    /// </summary>
    internal static TOptions RequireInterceptors<TOptions>(TOptions options) where TOptions : DbContextOptions
    {
        var interceptors = options.FindExtension<CoreOptionsExtension>()?.Interceptors ?? [];
        if (!interceptors.Contains(OutboxInterceptor.Instance))
        {
            throw new InvalidOperationException(
                $"{nameof(KvytokDbContext)} was configured without its outbox interceptor; " +
                $"call {nameof(AddKvytokInterceptors)}() where the context is registered.");
        }
        return options;
    }
}
