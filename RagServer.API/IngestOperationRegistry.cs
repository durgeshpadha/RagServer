using System.Collections.Concurrent;

public sealed class IngestOperationRegistry
{
    private readonly Lock _lock = new();
    private readonly ConcurrentDictionary<string, IngestOperation> _operations = new(StringComparer.OrdinalIgnoreCase);
    private IngestOperation? _active;

    public bool TryStart(out IngestOperation operation, out IngestOperation? conflict)
    {
        lock (_lock)
        {
            if (_active is not null && (_active.Status is IngestOperationStatus.Pending or IngestOperationStatus.Running))
            {
                operation = default!;
                conflict = _active;
                return false;
            }

            operation = new IngestOperation(Guid.NewGuid().ToString("N"));
            _operations[operation.OperationId] = operation;
            _active = operation;
            conflict = null;
            return true;
        }
    }

    public bool TryGet(string operationId, out IngestOperation operation)
    {
        return _operations.TryGetValue(operationId, out operation!);
    }

    public bool TryMarkRunning(IngestOperation operation)
    {
        lock (_lock)
        {
            if (operation.Status != IngestOperationStatus.Pending)
            {
                return false;
            }

            operation.Status = IngestOperationStatus.Running;
            operation.StartedAtUtc = DateTime.UtcNow;
            return true;
        }
    }

    public bool TryCancel(string operationId, out IngestOperation? operation)
    {
        if (!_operations.TryGetValue(operationId, out operation))
        {
            return false;
        }

        if (operation.Status is not (IngestOperationStatus.Pending or IngestOperationStatus.Running))
        {
            return true;
        }

        operation.Cancellation.Cancel();
        return true;
    }

    public void MarkCompleted(IngestOperation operation, IngestResponse summary)
    {
        lock (_lock)
        {
            operation.Status = IngestOperationStatus.Completed;
            operation.Summary = summary;
            operation.CompletedAtUtc = DateTime.UtcNow;
            if (_active?.OperationId == operation.OperationId)
            {
                _active = null;
            }
        }
    }

    public void MarkCanceled(IngestOperation operation)
    {
        lock (_lock)
        {
            operation.Status = IngestOperationStatus.Canceled;
            operation.CompletedAtUtc = DateTime.UtcNow;
            if (_active?.OperationId == operation.OperationId)
            {
                _active = null;
            }
        }
    }

    public void MarkFailed(IngestOperation operation, string errorMessage)
    {
        lock (_lock)
        {
            operation.Status = IngestOperationStatus.Failed;
            operation.ErrorMessage = errorMessage;
            operation.CompletedAtUtc = DateTime.UtcNow;
            if (_active?.OperationId == operation.OperationId)
            {
                _active = null;
            }
        }
    }
}

public sealed class IngestOperation
{
    public IngestOperation(string operationId)
    {
        OperationId = operationId;
    }

    public string OperationId { get; }
    public IngestOperationStatus Status { get; set; } = IngestOperationStatus.Pending;
    public CancellationTokenSource Cancellation { get; } = new();
    public DateTime CreatedAtUtc { get; } = DateTime.UtcNow;
    public DateTime? StartedAtUtc { get; set; }
    public DateTime? CompletedAtUtc { get; set; }
    public IngestResponse? Summary { get; set; }
    public string? ErrorMessage { get; set; }
}

public enum IngestOperationStatus
{
    Pending,
    Running,
    Completed,
    Canceled,
    Failed
}
