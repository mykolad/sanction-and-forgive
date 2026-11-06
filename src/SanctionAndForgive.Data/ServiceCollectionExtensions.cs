using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace SanctionAndForgive.Data;

public static class ServiceCollectionExtensions
{
    /// <summary>The lists database, with EF's retry on transient SQL failures. The connection string comes from
    /// <c>ConnectionStrings:Lists</c>: Entra ID only (<c>Authentication=Active Directory Default</c> locally,
    /// <c>Active Directory Managed Identity</c> in Azure).</summary>
    public static IServiceCollection AddListsDatabase(this IServiceCollection services, string? connectionString) =>
        services.AddDbContext<ListsDbContext>(options => options.UseSqlServer(
            connectionString,
            sql => sql.EnableRetryOnFailure().CommandTimeout(300)));
}
