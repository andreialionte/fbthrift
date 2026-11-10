// Copyright (c) 2024 FbThrift Contributors. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace FbThrift.LoadBalancing;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// Work request status lifecycle, following OCI SDK WorkRequest patterns.
/// </summary>
public enum WorkRequestStatus
{
    /// <summary>
    /// The work request has been accepted and is queued for processing.
    /// </summary>
    Accepted,

    /// <summary>
    /// The work request is currently being processed.
    /// </summary>
    InProgress,

    /// <summary>
    /// The work request completed successfully.
    /// </summary>
    Succeeded,

    /// <summary>
    /// The work request failed.
    /// </summary>
    Failed,

    /// <summary>
    /// The work request was canceled.
    /// </summary>
    Canceled,
}

/// <summary>
/// Type of work request, categorizing the async operation.
/// </summary>
public enum WorkRequestType
{
    /// <summary>
    /// Disaster recovery failover operation.
    /// </summary>
    DrFailover,

    /// <summary>
    /// Disaster recovery failback operation.
    /// </summary>
    DrFailback,

    /// <summary>
    /// Certificate rotation operation.
    /// </summary>
    CertificateRotation,

    /// <summary>
    /// Bulk data import operation.
    /// </summary>
    BulkImport,

    /// <summary>
    /// Custom operation type.
    /// </summary>
    Custom,
}

/// <summary>
/// A resource affected by a work request.
/// </summary>
public sealed class WorkRequestResource
{
    /// <summary>
    /// Gets or sets the resource type (e.g., "endpoint", "certificate", "protectionGroup").
    /// </summary>
    public string EntityType { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the resource identifier.
    /// </summary>
    public string Identifier { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the resource name.
    /// </summary>
    public string EntityName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the action being performed on this resource.
    /// </summary>
    public string ActionType { get; set; } = string.Empty;
}

/// <summary>
/// Error information for a failed work request.
/// </summary>
public sealed class WorkRequestError
{
    /// <summary>
    /// Gets or sets the error code.
    /// </summary>
    public string Code { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the error message.
    /// </summary>
    public string Message { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the timestamp when the error occurred.
    /// </summary>
    public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// Information about an asynchronous work request, following OCI SDK WorkRequest patterns.
/// </summary>
public sealed class WorkRequest
{
    /// <summary>
    /// Gets the unique work request identifier.
    /// </summary>
    public string Id { get; init; } = string.Empty;

    /// <summary>
    /// Gets the type of work request.
    /// </summary>
    public WorkRequestType OperationType { get; init; }

    /// <summary>
    /// Gets or sets the current status.
    /// </summary>
    public WorkRequestStatus Status { get; set; } = WorkRequestStatus.Accepted;

    /// <summary>
    /// Gets or sets the percent complete (0-100).
    /// </summary>
    public int PercentComplete { get; set; }

    /// <summary>
    /// Gets or sets the resources affected by this work request.
    /// </summary>
    public List<WorkRequestResource> Resources { get; set; } = new();

    /// <summary>
    /// Gets or sets any errors that occurred during processing.
    /// </summary>
    public List<WorkRequestError> Errors { get; set; } = new();

    /// <summary>
    /// Gets or sets the timestamp when the work request was created.
    /// </summary>
    public DateTimeOffset TimeAccepted { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Gets or sets the timestamp when the work request was last updated.
    /// </summary>
    public DateTimeOffset TimeUpdated { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Gets or sets the timestamp when the work request finished (succeeded/failed/canceled).
    /// </summary>
    public DateTimeOffset? TimeFinished { get; set; }

    /// <summary>
    /// Gets whether the work request is in a terminal state.
    /// </summary>
    public bool IsTerminal => Status is WorkRequestStatus.Succeeded or WorkRequestStatus.Failed or WorkRequestStatus.Canceled;

    /// <summary>
    /// Gets whether the work request completed successfully.
    /// </summary>
    public bool IsSuccessful => Status == WorkRequestStatus.Succeeded;
}

/// <summary>
/// Request to create a new work request.
/// </summary>
public sealed class CreateWorkRequestRequest
{
    /// <summary>
    /// Gets or sets the type of work request.
    /// </summary>
    public WorkRequestType OperationType { get; set; }

    /// <summary>
    /// Gets or sets the resources affected by this work request.
    /// </summary>
    public List<WorkRequestResource> Resources { get; set; } = new();

    /// <summary>
    /// Gets or sets arbitrary metadata.
    /// </summary>
    public Dictionary<string, string> Metadata { get; set; } = new();

    /// <summary>
    /// Gets or sets the cancellation token.
    /// </summary>
    public CancellationToken CancellationToken { get; set; }
}

/// <summary>
/// Response for creating a work request.
/// </summary>
public sealed class CreateWorkRequestResponse
{
    /// <summary>
    /// Gets or sets the created work request.
    /// </summary>
    public WorkRequest WorkRequest { get; set; } = new();

    /// <summary>
    /// Gets or sets the unique identifier for the response.
    /// </summary>
    public string? OpcRequestId { get; set; }
}

/// <summary>
/// Request to get a work request by ID.
/// </summary>
public sealed class GetWorkRequestRequest
{
    /// <summary>
    /// Gets or sets the work request ID.
    /// </summary>
    public string WorkRequestId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the cancellation token.
    /// </summary>
    public CancellationToken CancellationToken { get; set; }
}

/// <summary>
/// Response for getting a work request.
/// </summary>
public sealed class GetWorkRequestResponse
{
    /// <summary>
    /// Gets or sets the work request, or null if not found.
    /// </summary>
    public WorkRequest? WorkRequest { get; set; }

    /// <summary>
    /// Gets or sets the unique identifier for the response.
    /// </summary>
    public string? OpcRequestId { get; set; }
}

/// <summary>
/// Manager for tracking asynchronous work requests, following OCI SDK WorkRequest patterns.
/// Integrates with the existing Waiter<TRequest, TResponse> pattern for polling.
/// </summary>
public sealed class WorkRequestManager : IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, WorkRequest> _workRequests = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();
    private bool _disposed;

    /// <summary>
    /// Creates a new work request.
    /// </summary>
    public CreateWorkRequestResponse CreateWorkRequest(CreateWorkRequestRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var workRequest = new WorkRequest
        {
            Id = Guid.NewGuid().ToString("N"),
            OperationType = request.OperationType,
            Status = WorkRequestStatus.Accepted,
            PercentComplete = 0,
            Resources = new List<WorkRequestResource>(request.Resources),
            TimeAccepted = DateTimeOffset.UtcNow,
            TimeUpdated = DateTimeOffset.UtcNow,
        };

        _workRequests[workRequest.Id] = workRequest;

        return new CreateWorkRequestResponse
        {
            WorkRequest = workRequest,
            OpcRequestId = Guid.NewGuid().ToString("N"),
        };
    }

    /// <summary>
    /// Gets a work request by ID.
    /// </summary>
    public ValueTask<GetWorkRequestResponse> GetWorkRequestAsync(GetWorkRequestRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        WorkRequest? workRequest;
        lock (_lock)
        {
            workRequest = _workRequests.TryGetValue(request.WorkRequestId, out var wr) ? wr : null;
        }

        return ValueTask.FromResult(new GetWorkRequestResponse
        {
            WorkRequest = workRequest,
            OpcRequestId = workRequest is null ? null : Guid.NewGuid().ToString("N"),
        });
    }

    /// <summary>
    /// Updates the status and progress of a work request.
    /// </summary>
    public bool UpdateWorkRequest(
        string workRequestId,
        WorkRequestStatus status,
        int percentComplete,
        List<WorkRequestError>? errors = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workRequestId);

        if (percentComplete < 0 || percentComplete > 100)
            throw new ArgumentOutOfRangeException(nameof(percentComplete), "Percent complete must be between 0 and 100.");

        lock (_lock)
        {
            if (!_workRequests.TryGetValue(workRequestId, out var workRequest))
                return false;

            workRequest.Status = status;
            workRequest.PercentComplete = percentComplete;
            workRequest.TimeUpdated = DateTimeOffset.UtcNow;

            if (errors != null && errors.Count > 0)
                workRequest.Errors = errors;

            if (status is WorkRequestStatus.Succeeded or WorkRequestStatus.Failed or WorkRequestStatus.Canceled)
                workRequest.TimeFinished = DateTimeOffset.UtcNow;

            return true;
        }
    }

    /// <summary>
    /// Increments the percent complete of a work request.
    /// </summary>
    public bool UpdateProgress(string workRequestId, int percentComplete)
    {
        if (!_workRequests.TryGetValue(workRequestId, out var workRequest))
            return false;

        workRequest.PercentComplete = percentComplete;
        workRequest.TimeUpdated = DateTimeOffset.UtcNow;
        return true;
    }

    /// <summary>
    /// Gets all work requests.
    /// </summary>
    public IReadOnlyList<WorkRequest> GetWorkRequests()
    {
        lock (_lock)
        {
            return _workRequests.Values.ToList().AsReadOnly();
        }
    }

    /// <summary>
    /// Gets work requests by status.
    /// </summary>
    public IReadOnlyList<WorkRequest> GetWorkRequestsByStatus(WorkRequestStatus status)
    {
        lock (_lock)
        {
            return _workRequests.Values.Where(w => w.Status == status).ToList().AsReadOnly();
        }
    }

    /// <summary>
    /// Gets work requests by type.
    /// </summary>
    public IReadOnlyList<WorkRequest> GetWorkRequestsByType(WorkRequestType type)
    {
        lock (_lock)
        {
            return _workRequests.Values.Where(w => w.OperationType == type).ToList().AsReadOnly();
        }
    }

    /// <summary>
    /// Cancels a work request if it's still in progress.
    /// </summary>
    public bool CancelWorkRequest(string workRequestId)
    {
        lock (_lock)
        {
            if (!_workRequests.TryGetValue(workRequestId, out var workRequest))
                return false;

            if (workRequest.IsTerminal)
                return false;

            workRequest.Status = WorkRequestStatus.Canceled;
            workRequest.TimeUpdated = DateTimeOffset.UtcNow;
            workRequest.TimeFinished = DateTimeOffset.UtcNow;
            return true;
        }
    }

    /// <summary>
    /// Removes a work request from tracking.
    /// </summary>
    public bool RemoveWorkRequest(string workRequestId)
    {
        lock (_lock)
        {
            return _workRequests.TryRemove(workRequestId, out _);
        }
    }

    /// <summary>
    /// Disposes the work request manager.
    /// </summary>
    public ValueTask DisposeAsync()
    {
        if (!_disposed)
        {
            _disposed = true;
            lock (_lock)
            {
                _workRequests.Clear();
            }
        }
        return ValueTask.CompletedTask;
    }
}
