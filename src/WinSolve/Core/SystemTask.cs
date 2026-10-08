namespace WinSolve.Core;

public enum TaskCategory
{
    Cleanup,
    Performance,
    Repair,
    Network,
    Security,
}

/// <summary>Context passed to every task: output log and cancellation.</summary>
public sealed class TaskContext(Action<string> log, CancellationToken ct)
{
    public CancellationToken Token { get; } = ct;

    /// <summary>Bytes freed by cleanup tasks (accumulated).</summary>
    public long FreedBytes { get; set; }

    public bool RebootRecommended { get; set; }

    public void Log(string message) => log(message);
}

/// <summary>A runnable maintenance action (cleanup, repair, optimization...).</summary>
public sealed class SystemTask
{
    public required string Id { get; init; }
    public required string Title { get; init; }
    public required string Description { get; init; }
    public required TaskCategory Category { get; init; }
    public required Func<TaskContext, Task> Run { get; init; }

    /// <summary>Included in the default custom one-click list.</summary>
    public bool Recommended { get; init; }

    /// <summary>Takes several minutes.</summary>
    public bool Slow { get; init; }

    public bool NeedsReboot { get; init; }

    public override string ToString() => Localization.Loc.T(Title);
}
