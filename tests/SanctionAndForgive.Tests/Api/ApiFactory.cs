using System.Data.Common;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using Microsoft.Extensions.Time.Testing;
using SanctionAndForgive.Api.Lists;
using SanctionAndForgive.Data;
using SanctionAndForgive.Matching;

namespace SanctionAndForgive.Tests.Api;

/// <summary>The real Program in memory: environment Testing (never Development, which would load user secrets), the
/// list refresh off, a fake clock, and a database that counts every connection attempt: a screening must make none.</summary>
public sealed class ApiFactory(int rateLimitPerMinute) : WebApplicationFactory<Program>
{
    public static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-11-06T12:00:00Z", System.Globalization.CultureInfo.InvariantCulture);

    public ApiFactory()
        : this(0)
    {
    }

    public FakeTimeProvider Clock { get; } = new(Now);

    public ConnectionCounter Database { get; } = new();

    /// <summary>Every log record the app writes, whatever its category: the privacy tests read them all.</summary>
    public FakeLogCollector Logs { get; } = new();

    public ListCatalog Catalog => Services.GetRequiredService<ListCatalog>();

    /// <summary>Puts lists in memory as the loader would after a refresh. A null index means it failed to build.</summary>
    public void Load(params LoadedList[] lists) => Catalog.Replace(lists);

    public static LoadedList Feed(string code, string name, DateTimeOffset? lastChecked, params ListedSubject[] subjects) =>
        new(code, name, $"https://example.invalid/{code}", 72, 1, lastChecked, lastChecked?.AddDays(-3), subjects.Length, ListIndex.Build(subjects));

    public static ListedSubject Subject(string name, SubjectKind kind) =>
        new($"X-{name.Length}-{name[0]}", kind, name, [new ListedName(name, IsWeak: false)], [], "TEST", "https://example.invalid/record");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("Lists:RefreshEnabled", "false");
        builder.UseSetting("RateLimiting:PerMinute", rateLimitPerMinute.ToString(System.Globalization.CultureInfo.InvariantCulture));
        builder.UseSetting("ConnectionStrings:Lists", "Server=tcp:127.0.0.1,1;Database=none;Connect Timeout=1");
        builder.ConfigureLogging(logging => logging.AddProvider(new FakeLoggerProvider(Logs)));
        builder.ConfigureTestServices(services =>
        {
            services.AddSingleton<TimeProvider>(Clock);
            services.RemoveAll<DbContextOptions<ListsDbContext>>();
            services.AddDbContext<ListsDbContext>(options => options.UseSqlServer("Server=tcp:127.0.0.1,1;Database=none;Connect Timeout=1").AddInterceptors(Database));
        });
    }
}

public sealed class ConnectionCounter : DbConnectionInterceptor
{
    private int opened;

    public int Opened => opened;

    public override ValueTask<InterceptionResult> ConnectionOpeningAsync(DbConnection connection, ConnectionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref opened);
        return ValueTask.FromResult(result);
    }

    public override InterceptionResult ConnectionOpening(DbConnection connection, ConnectionEventData eventData, InterceptionResult result)
    {
        Interlocked.Increment(ref opened);
        return result;
    }
}

internal static class ServiceCollectionTestExtensions
{
    public static void RemoveAll<T>(this IServiceCollection services)
    {
        foreach (var descriptor in services.Where(descriptor => descriptor.ServiceType == typeof(T)).ToList())
        {
            services.Remove(descriptor);
        }
    }
}
