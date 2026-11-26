namespace Cadflow.Domain.Jobs;

public enum JobStatus { Queued, Running, Succeeded, Failed, Cancelled }

public static class JobKinds
{
    /// <summary>Full build: model, STEP, pictures, drawing.</summary>
    public const string Generate = "generate";
    /// <summary>Fast build for the screen: one picture only.</summary>
    public const string Preview = "preview";
    /// <summary>Rebuild of an older job (same values).</summary>
    public const string Export = "export";
    public static readonly string[] All = [Generate, Preview, Export];
    public static bool IsKnown(string? kind) => kind != null && All.Contains(kind, StringComparer.Ordinal);
}

public static class JobQueues
{
    public const string Build = "build";
    public const string Filter = "filter";
}

/// <summary>Kind -> queue and priority (0 = most urgent, 100 = least). Pure rule, no configuration source.</summary>
public static class QueuePolicy
{
    public const int DefaultPriority = 20;
    public static (string Queue, int Priority) Resolve(string kind) => kind switch
    {
        JobKinds.Preview => (JobQueues.Filter, 0),
        _ => (JobQueues.Build, DefaultPriority),
    };
}

public sealed record JobEvent(DateTimeOffset Time, string Message);

public sealed record ArtifactInfo(string Name, string Kind, long Size);

/// <summary>Flat copy of a job for persistence adapters (the aggregate itself keeps private setters).</summary>
public sealed record JobSnapshot(
    string Id, string ProjectId, string ProductCode, string Kind, string Queue, int Priority,
    IReadOnlyDictionary<string, double> Inputs, string RequestedBy, JobStatus Status, string? Step, string? Worker, int Attempts, string? Error,
    DateTimeOffset CreatedAt, DateTimeOffset? StartedAt, DateTimeOffset? FinishedAt, DateTimeOffset? LastHeartbeat,
    IReadOnlyDictionary<string, double> Summary, IReadOnlyList<ArtifactInfo> Artifacts, IReadOnlyList<JobEvent> Events,
    IReadOnlyDictionary<string, string>? TextInputs = null);

public sealed class DomainException(string message) : Exception(message);

/// <summary>A request to build one product. Owns its own state transitions; knows nothing about storage or HTTP.</summary>
public sealed class Job
{
    public const int MaxAttempts = 3;

    public string Id { get; private set; } = "";
    public string ProjectId { get; private set; } = "";
    public string ProductCode { get; private set; } = "";
    public string Kind { get; private set; } = JobKinds.Generate;
    public string Queue { get; private set; } = JobQueues.Build;
    public int Priority { get; private set; } = QueuePolicy.DefaultPriority;
    public IReadOnlyDictionary<string, double> Inputs { get; private set; } = new Dictionary<string, double>();
    /// <summary>Non-numeric choices of the order (part information: PI_Material, PI_Finish).</summary>
    public IReadOnlyDictionary<string, string> TextInputs { get; private set; } = new Dictionary<string, string>();
    public string RequestedBy { get; private set; } = "";
    public JobStatus Status { get; private set; } = JobStatus.Queued;
    public string? Step { get; private set; }
    public string? Worker { get; private set; }
    public int Attempts { get; private set; }
    public string? Error { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset? StartedAt { get; private set; }
    public DateTimeOffset? FinishedAt { get; private set; }
    public DateTimeOffset? LastHeartbeat { get; private set; }
    public IReadOnlyDictionary<string, double> Summary { get; private set; } = new Dictionary<string, double>();
    private readonly List<ArtifactInfo> _artifacts = new();
    private readonly List<JobEvent> _events = new();
    public IReadOnlyList<ArtifactInfo> Artifacts => _artifacts;
    public IReadOnlyList<JobEvent> Events => _events;

    private Job() { }

    public static Job Restore(JobSnapshot s)
    {
        var j = new Job
        {
            Id = s.Id, ProjectId = s.ProjectId, ProductCode = s.ProductCode, Kind = s.Kind, Queue = s.Queue, Priority = s.Priority,
            Inputs = s.Inputs, RequestedBy = s.RequestedBy, Status = s.Status, Step = s.Step, Worker = s.Worker, Attempts = s.Attempts, Error = s.Error,
            CreatedAt = s.CreatedAt, StartedAt = s.StartedAt, FinishedAt = s.FinishedAt, LastHeartbeat = s.LastHeartbeat, Summary = s.Summary,
            TextInputs = s.TextInputs ?? new Dictionary<string, string>(),
        };
        j._artifacts.AddRange(s.Artifacts);
        j._events.AddRange(s.Events);
        return j;
    }

    public JobSnapshot ToSnapshot() => new(Id, ProjectId, ProductCode, Kind, Queue, Priority, Inputs, RequestedBy, Status, Step, Worker, Attempts, Error,
        CreatedAt, StartedAt, FinishedAt, LastHeartbeat, Summary, _artifacts.ToList(), _events.ToList(), TextInputs);

    public static Job Create(string id, string projectId, string productCode, string kind, IReadOnlyDictionary<string, double> inputs, string requestedBy, DateTimeOffset now,
        IReadOnlyDictionary<string, string>? textInputs = null)
    {
        if (!JobKinds.IsKnown(kind)) throw new DomainException($"Unknown job kind '{kind}'.");
        var (queue, priority) = QueuePolicy.Resolve(kind);
        var j = new Job
        {
            Id = id, ProjectId = projectId, ProductCode = productCode, Kind = kind, Queue = queue, Priority = priority,
            Inputs = new Dictionary<string, double>(inputs), RequestedBy = requestedBy, CreatedAt = now,
            TextInputs = new Dictionary<string, string>(textInputs ?? new Dictionary<string, string>()),
        };
        j.Log(now, "Queued.");
        return j;
    }

    public void Start(string worker, DateTimeOffset now)
    {
        if (Status != JobStatus.Queued) throw new DomainException($"Job {Id} is {Status}, not queued.");
        Status = JobStatus.Running; Worker = worker; Attempts++; StartedAt ??= now; LastHeartbeat = now; Error = null;
        Log(now, $"Started by {worker} (attempt {Attempts}).");
    }

    public void ReportProgress(string worker, string? step, string? message, DateTimeOffset now)
    {
        EnsureRunningBy(worker);
        LastHeartbeat = now;
        if (!string.IsNullOrWhiteSpace(step)) Step = step;
        if (!string.IsNullOrWhiteSpace(message)) Log(now, message!);
    }

    public void AddArtifact(string worker, ArtifactInfo artifact)
    {
        EnsureRunningBy(worker);
        _artifacts.RemoveAll(a => a.Name == artifact.Name);
        _artifacts.Add(artifact);
    }

    public void Complete(string worker, IReadOnlyDictionary<string, double> summary, DateTimeOffset now)
    {
        EnsureRunningBy(worker);
        Status = JobStatus.Succeeded; Summary = new Dictionary<string, double>(summary); FinishedAt = now; Step = null;
        Log(now, "Succeeded.");
    }

    /// <summary>Retryable failures go back to the queue until <see cref="MaxAttempts"/>; otherwise the job fails for good.</summary>
    public void Fail(string worker, string error, bool retryable, DateTimeOffset now)
    {
        EnsureRunningBy(worker);
        Error = error;
        if (retryable && Attempts < MaxAttempts)
        {
            Status = JobStatus.Queued; Worker = null; Step = null;
            Log(now, $"Failed ({error}); queued for retry.");
        }
        else
        {
            Status = JobStatus.Failed; FinishedAt = now;
            Log(now, $"Failed: {error}");
        }
    }

    /// <summary>Adds a line to the job log without changing its state (e.g. the outcome of sending results to the Vault).</summary>
    public void Note(string message, DateTimeOffset now) => Log(now, message);

    public void Cancel(DateTimeOffset now)
    {
        if (Status is JobStatus.Succeeded or JobStatus.Failed or JobStatus.Cancelled) throw new DomainException($"Job {Id} is already {Status}.");
        Status = JobStatus.Cancelled; FinishedAt = now; Log(now, "Cancelled.");
    }

    /// <summary>A running job whose worker went quiet is put back in the queue.</summary>
    public bool ReclaimIfStale(TimeSpan timeout, DateTimeOffset now)
    {
        if (Status != JobStatus.Running || LastHeartbeat is not { } hb || now - hb < timeout) return false;
        Fail(Worker ?? "", "Worker stopped responding.", retryable: true, now);
        return true;
    }

    private void EnsureRunningBy(string worker)
    {
        if (Status != JobStatus.Running || Worker != worker) throw new DomainException($"Job {Id} is not running on worker '{worker}'.");
    }

    private void Log(DateTimeOffset t, string m) => _events.Add(new JobEvent(t, m));
}


