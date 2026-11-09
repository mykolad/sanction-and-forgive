using Microsoft.EntityFrameworkCore;

namespace SanctionAndForgive.Data;

public enum SwitchResult
{
    /// <summary>Switched as asked.</summary>
    Switched,

    /// <summary>It was already that way: nothing changed (an off list keeps its first reason and time).</summary>
    Unchanged,

    NotFound,
}

/// <summary>Switching a list off and on: an administrator's decision (owner's decision, 2026-11-09). An off list
/// isn't searched, but it's still imported, shown on the status page and browsable, so switching it on again
/// searches it as up to date as the others.</summary>
public sealed class SourceSwitch(ListsDbContext context, TimeProvider clock)
{
    public const int MaxReason = 500;

    /// <summary>Switches <paramref name="code"/> off, with <paramref name="reason"/>, or on (<paramref name="reason"/>
    /// ignored). One conditional update: two administrators at once can't both switch it.</summary>
    public async Task<SwitchResult> SwitchAsync(string code, bool off, string? reason, string by, CancellationToken cancellationToken)
    {
        var sources = context.Sources.Where(source => source.Code == code);
        var changed = off
            ? await sources.Where(source => source.SwitchedOffAt == null).ExecuteUpdateAsync(
                setters => setters.SetProperty(source => source.SwitchedOffAt, clock.GetUtcNow())
                    .SetProperty(source => source.SwitchedOffBy, by)
                    .SetProperty(source => source.SwitchedOffReason, reason),
                cancellationToken)
            : await sources.Where(source => source.SwitchedOffAt != null).ExecuteUpdateAsync(
                setters => setters.SetProperty(source => source.SwitchedOffAt, (DateTimeOffset?)null)
                    .SetProperty(source => source.SwitchedOffBy, (string?)null)
                    .SetProperty(source => source.SwitchedOffReason, (string?)null),
                cancellationToken);
        if (changed > 0)
        {
            return SwitchResult.Switched;
        }

        return await sources.AnyAsync(cancellationToken) ? SwitchResult.Unchanged : SwitchResult.NotFound;
    }
}
