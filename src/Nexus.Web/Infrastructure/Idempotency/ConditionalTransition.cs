namespace Nexus.Web.Infrastructure.Idempotency;

public enum TransitionOutcome
{
    Succeeded,
    AlreadyInTargetState,
    Conflict,
    NotFound
}

public sealed record TransitionResult<TState>(TransitionOutcome Outcome, TState? CurrentState)
{
    public bool IsSuccess => Outcome is TransitionOutcome.Succeeded or TransitionOutcome.AlreadyInTargetState;
}

/// <summary>
/// Reusable conditional state-transition helper (M0.5 / docs/SYNC_PROTOCOL.md §5).
/// Executes a single conditional UPDATE ... WHERE status = @expected, then inspects current
/// state when 0 rows changed to distinguish idempotent replay from a conflicting transition (409).
/// </summary>
public static class ConditionalTransition
{
    public static async Task<TransitionResult<TState>> ExecuteAsync<TState>(
        Func<CancellationToken, Task<int>> conditionalUpdateAsync,
        Func<CancellationToken, Task<(bool Found, TState? CurrentState)>> readCurrentStateAsync,
        TState targetState,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(conditionalUpdateAsync);
        ArgumentNullException.ThrowIfNull(readCurrentStateAsync);

        var affected = await conditionalUpdateAsync(cancellationToken);
        if (affected == 1)
        {
            return new TransitionResult<TState>(TransitionOutcome.Succeeded, targetState);
        }

        var (found, current) = await readCurrentStateAsync(cancellationToken);
        if (!found)
        {
            return new TransitionResult<TState>(TransitionOutcome.NotFound, default);
        }

        if (EqualityComparer<TState>.Default.Equals(current, targetState))
        {
            return new TransitionResult<TState>(TransitionOutcome.AlreadyInTargetState, current);
        }

        return new TransitionResult<TState>(TransitionOutcome.Conflict, current);
    }
}
