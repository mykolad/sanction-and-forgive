using SanctionAndForgive.Api.Lists;
using SanctionAndForgive.Data;

namespace SanctionAndForgive.Api.Admin;

/// <summary>Switches a list off (not searched, still imported, shown and browsable) or on again, then reloads the
/// lists so it applies at once.</summary>
public interface IListSwitch
{
    Task<SwitchOutcome> SwitchAsync(string code, bool off, string? reason, string by, CancellationToken cancellationToken);
}

/// <param name="Reloaded">Whether the lists were reloaded afterwards. False: the switch stands (it's committed), and
/// the next scheduled refresh, within 15 minutes, applies it.</param>
public sealed record SwitchOutcome(SwitchResult Result, bool Reloaded);

/// <summary>Through <see cref="SourceSwitch"/> (one conditional update), then one refresh, <see cref="ListLoader"/>'s
/// own. The switch is committed before the refresh starts, so a refresh that fails is reported as such.</summary>
public sealed partial class ListSwitch(IServiceScopeFactory scopes, ListLoader loader, TimeProvider clock, ILogger<ListSwitch> logger) : IListSwitch
{
    public async Task<SwitchOutcome> SwitchAsync(string code, bool off, string? reason, string by, CancellationToken cancellationToken)
    {
        SwitchResult result;
        await using (var scope = scopes.CreateAsyncScope())
        {
            result = await new SourceSwitch(scope.ServiceProvider.GetRequiredService<ListsDbContext>(), clock).SwitchAsync(code, off, reason, by, cancellationToken);
        }

        if (result != SwitchResult.Switched)
        {
            return new SwitchOutcome(result, Reloaded: false);
        }

        try
        {
            await loader.RefreshAsync(cancellationToken);
            return new SwitchOutcome(result, Reloaded: true);
        }
        // Any failure after the commit, a cancelled request included, is a reload that didn't happen: the switch
        // stands, and the controller still logs it.
        catch (Exception exception)
        {
            ReloadFailed(logger, exception, code);
            return new SwitchOutcome(result, Reloaded: false);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "List {Code} was switched, but reloading the lists afterwards failed; the next refresh applies it")]
    private static partial void ReloadFailed(ILogger logger, Exception exception, string code);
}
