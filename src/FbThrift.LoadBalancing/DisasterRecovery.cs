// Copyright (c) 2024 FbThrift Contributors. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace FbThrift.LoadBalancing;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// Lifecycle states for disaster recovery resources, following OCI SDK lifecycle patterns.
/// </summary>
public enum DrResourceLifecycleState
{
    Creating,
    Active,
    Updating,
    Deleting,
    Deleted,
    Failed,
}

/// <summary>
/// Type of disaster recovery resource.
/// </summary>
public enum DrResourceType
{
    ProtectionGroup,
    DrPlan,
    DrPlanExecution,
}

/// <summary>
/// Information about a disaster recovery resource.
/// </summary>
public sealed class DrResourceInfo
{
    /// <summary>
    /// Gets the resource ID.
    /// </summary>
    public string Id { get; init; } = string.Empty;

    /// <summary>
    /// Gets the resource type.
    /// </summary>
    public DrResourceType Type { get; init; }

    /// <summary>
    /// Gets the current lifecycle state.
    /// </summary>
    public DrResourceLifecycleState LifecycleState { get; set; } = DrResourceLifecycleState.Creating;

    /// <summary>
    /// Gets or sets the display name.
    /// </summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the region where the resource resides.
    /// </summary>
    public string Region { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets arbitrary metadata.
    /// </summary>
    public Dictionary<string, string> Metadata { get; } = new();

    /// <summary>
    /// Gets or sets the timestamp when the resource was created.
    /// </summary>
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Gets or sets the timestamp when the resource was last updated.
    /// </summary>
    public DateTimeOffset? UpdatedAt { get; set; }
}

/// <summary>
/// Base class for DR requests, following OCI SDK request/response model.
/// </summary>
public abstract class DrRequest
{
    /// <summary>
    /// Gets or sets the request ID for idempotency.
    /// </summary>
    public string? RequestId { get; set; }

    /// <summary>
    /// Gets or sets the retry configuration for this request.
    /// </summary>
    public RetryConfiguration? RetryConfiguration { get; set; }

    /// <summary>
    /// Gets or sets the cancellation token.
    /// </summary>
    public CancellationToken CancellationToken { get; set; }
}

/// <summary>
/// Base class for DR responses, following OCI SDK request/response model.
/// </summary>
public abstract class DrResponse
{
    /// <summary>
    /// Gets or sets the unique identifier for the response.
    /// </summary>
    public string? OpcRequestId { get; set; }

    /// <summary>
    /// Gets or sets the ETag for optimistic concurrency control.
    /// </summary>
    public string? ETag { get; set; }
}

/// <summary>
/// Request to create a DR protection group.
/// </summary>
public sealed class CreateDrProtectionGroupRequest : DrRequest
{
    /// <summary>
    /// Gets or sets the protection group name.
    /// </summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the primary region.
    /// </summary>
    public string PrimaryRegion { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the standby region.
    /// </summary>
    public string StandbyRegion { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the endpoints in the primary region.
    /// </summary>
    public List<string> PrimaryEndpoints { get; set; } = new();

    /// <summary>
    /// Gets or sets the endpoints in the standby region.
    /// </summary>
    public List<string> StandbyEndpoints { get; set; } = new();
}

/// <summary>
/// Response for creating a DR protection group.
/// </summary>
public sealed class CreateDrProtectionGroupResponse : DrResponse
{
    /// <summary>
    /// Gets or sets the created protection group info.
    /// </summary>
    public DrResourceInfo? ProtectionGroup { get; set; }
}

/// <summary>
/// Request to failover from primary to standby region.
/// </summary>
public sealed class FailoverRequest : DrRequest
{
    /// <summary>
    /// Gets or sets the protection group ID to failover.
    /// </summary>
    public string ProtectionGroupId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the target standby region to failover to.
    /// </summary>
    public string StandbyRegion { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets whether to force failover even if primary is healthy.
    /// </summary>
    public bool Force { get; set; }
}

/// <summary>
/// Response for failover operation.
/// </summary>
public sealed class FailoverResponse : DrResponse
{
    /// <summary>
    /// Gets or sets whether the failover was successful.
    /// </summary>
    public bool Success { get; set; }

    /// <summary>
    /// Gets or sets the region that is now active.
    /// </summary>
    public string ActiveRegion { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the failover execution ID.
    /// </summary>
    public string ExecutionId { get; set; } = string.Empty;
}

/// <summary>
/// Request to failback from standby to primary region.
/// </summary>
public sealed class FailbackRequest : DrRequest
{
    /// <summary>
    /// Gets or sets the protection group ID to failback.
    /// </summary>
    public string ProtectionGroupId { get; set; } = string.Empty;
}

/// <summary>
/// Response for failback operation.
/// </summary>
public sealed class FailbackResponse : DrResponse
{
    /// <summary>
    /// Gets or sets whether the failback was successful.
    /// </summary>
    public bool Success { get; set; }

    /// <summary>
    /// Gets or sets the region that is now active.
    /// </summary>
    public string ActiveRegion { get; set; } = string.Empty;
}

/// <summary>
/// Disaster Recovery manager for fbthrift, following OCI SDK DisasterRecoveryClient pattern.
/// Provides failover/failback between primary and standby regions.
/// </summary>
public sealed class DisasterRecoveryManager : IAsyncDisposable
{
    private readonly ILoadBalancer _loadBalancer;
    private readonly WorkRequestManager _workRequestManager;
    private readonly Dictionary<string, DrResourceInfo> _protectionGroups = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();
    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of <see cref="DisasterRecoveryManager"/>.
    /// </summary>
    /// <param name="loadBalancer">The load balancer to use for endpoint selection.</param>
    /// <param name="workRequestManager">The work request manager for tracking async operations.</param>
    public DisasterRecoveryManager(ILoadBalancer loadBalancer, WorkRequestManager? workRequestManager = null)
    {
        _loadBalancer = loadBalancer ?? throw new ArgumentNullException(nameof(loadBalancer));
        _workRequestManager = workRequestManager ?? new WorkRequestManager();
    }

    /// <summary>
    /// Creates a DR protection group with primary and standby endpoints.
    /// </summary>
    public async ValueTask<CreateDrProtectionGroupResponse> CreateProtectionGroupAsync(
        CreateDrProtectionGroupRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var group = new DrResourceInfo
        {
            Id = Guid.NewGuid().ToString("N"),
            Type = DrResourceType.ProtectionGroup,
            DisplayName = request.DisplayName,
            Region = request.PrimaryRegion,
            LifecycleState = DrResourceLifecycleState.Active,
            Metadata =
            {
                ["PrimaryRegion"] = request.PrimaryRegion,
                ["StandbyRegion"] = request.StandbyRegion,
                ["PrimaryEndpoints"] = string.Join(",", request.PrimaryEndpoints),
                ["StandbyEndpoints"] = string.Join(",", request.StandbyEndpoints),
            },
        };

        lock (_lock)
        {
            _protectionGroups[group.Id] = group;
        }

        // Register primary endpoints with load balancer
        foreach (var endpoint in request.PrimaryEndpoints)
        {
            await _loadBalancer.AddEndpointAsync(
                endpoint,
                weight: 1.0,
                new Dictionary<string, string> { ["region"] = request.PrimaryRegion, ["role"] = "primary" },
                request.CancellationToken).ConfigureAwait(false);
        }

        // Register standby endpoints with load balancer (marked unhealthy initially)
        foreach (var endpoint in request.StandbyEndpoints)
        {
            await _loadBalancer.AddEndpointAsync(
                endpoint,
                weight: 1.0,
                new Dictionary<string, string> { ["region"] = request.StandbyRegion, ["role"] = "standby" },
                request.CancellationToken).ConfigureAwait(false);
            await _loadBalancer.SetEndpointHealthAsync(endpoint, false, request.CancellationToken).ConfigureAwait(false);
        }

        return new CreateDrProtectionGroupResponse
        {
            OpcRequestId = request.RequestId ?? Guid.NewGuid().ToString("N"),
            ProtectionGroup = group,
        };
    }

    /// <summary>
    /// Fails over from primary to standby region asynchronously.
    /// Returns a WorkRequest that clients can poll using the Waiter pattern.
    /// </summary>
    public async ValueTask<FailoverResponse> FailoverAsync(FailoverRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ProtectionGroupId);

        // Create work request for tracking
        var workRequestResponse = _workRequestManager.CreateWorkRequest(new CreateWorkRequestRequest
        {
            OperationType = WorkRequestType.DrFailover,
            Resources = new List<WorkRequestResource>
            {
                new()
                {
                    EntityType = "protectionGroup",
                    Identifier = request.ProtectionGroupId,
                    EntityName = request.ProtectionGroupId,
                    ActionType = "failover",
                },
            },
        });

        var workRequest = workRequestResponse.WorkRequest;

        // Update work request to InProgress
        _workRequestManager.UpdateWorkRequest(
            workRequest.Id,
            WorkRequestStatus.InProgress,
            10);

        lock (_lock)
        {
            if (!_protectionGroups.TryGetValue(request.ProtectionGroupId, out var group))
            {
                _workRequestManager.UpdateWorkRequest(
                    workRequest.Id,
                    WorkRequestStatus.Failed,
                    0,
                    new List<WorkRequestError>
                    {
                        new()
                        {
                            Code = "ProtectionGroupNotFound",
                            Message = $"Protection group '{request.ProtectionGroupId}' not found.",
                            Timestamp = DateTimeOffset.UtcNow,
                        },
                    });

                throw new KeyNotFoundException($"Protection group '{request.ProtectionGroupId}' not found.");
            }

            if (group.LifecycleState != DrResourceLifecycleState.Active)
            {
                _workRequestManager.UpdateWorkRequest(
                    workRequest.Id,
                    WorkRequestStatus.Failed,
                    0,
                    new List<WorkRequestError>
                    {
                        new()
                        {
                            Code = "InvalidState",
                            Message = $"Protection group is in {group.LifecycleState} state, cannot failover.",
                            Timestamp = DateTimeOffset.UtcNow,
                        },
                    });

                throw new InvalidOperationException($"Protection group is in {group.LifecycleState} state, cannot failover.");
            }

            group.Metadata["ActiveRegion"] = request.StandbyRegion;
            group.Metadata["PreviousActiveRegion"] = group.Region;
            group.Region = request.StandbyRegion;
            group.UpdatedAt = DateTimeOffset.UtcNow;
            group.LifecycleState = DrResourceLifecycleState.Updating;
        }

        try
        {
            // Mark standby endpoints as healthy (30% progress)
            var standbyEndpoints = GetStandbyEndpoints(request.ProtectionGroupId);
            foreach (var endpoint in standbyEndpoints)
            {
                await _loadBalancer.SetEndpointHealthAsync(endpoint, true, request.CancellationToken).ConfigureAwait(false);
            }

            _workRequestManager.UpdateWorkRequest(workRequest.Id, WorkRequestStatus.InProgress, 50);

            // Mark primary endpoints as unhealthy (80% progress)
            var primaryEndpoints = GetPrimaryEndpoints(request.ProtectionGroupId);
            foreach (var endpoint in primaryEndpoints)
            {
                await _loadBalancer.SetEndpointHealthAsync(endpoint, false, request.CancellationToken).ConfigureAwait(false);
            }

            _workRequestManager.UpdateWorkRequest(workRequest.Id, WorkRequestStatus.InProgress, 90);

            lock (_lock)
            {
                _protectionGroups[request.ProtectionGroupId].LifecycleState = DrResourceLifecycleState.Active;
            }

            // Mark work request as succeeded
            _workRequestManager.UpdateWorkRequest(workRequest.Id, WorkRequestStatus.Succeeded, 100);

            return new FailoverResponse
            {
                OpcRequestId = request.RequestId ?? Guid.NewGuid().ToString("N"),
                Success = true,
                ActiveRegion = request.StandbyRegion,
                ExecutionId = workRequest.Id,
            };
        }
        catch (Exception ex)
        {
            _workRequestManager.UpdateWorkRequest(
                workRequest.Id,
                WorkRequestStatus.Failed,
                0,
                new List<WorkRequestError>
                {
                    new()
                    {
                        Code = "FailoverFailed",
                        Message = ex.Message,
                        Timestamp = DateTimeOffset.UtcNow,
                    },
                });

            lock (_lock)
            {
                _protectionGroups[request.ProtectionGroupId].LifecycleState = DrResourceLifecycleState.Failed;
            }
            throw;
        }
    }

    /// <summary>
    /// Fails back from standby to primary region asynchronously.
    /// Returns a WorkRequest that clients can poll using the Waiter pattern.
    /// </summary>
    public async ValueTask<FailbackResponse> FailbackAsync(FailbackRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ProtectionGroupId);

        // Create work request for tracking
        var workRequestResponse = _workRequestManager.CreateWorkRequest(new CreateWorkRequestRequest
        {
            OperationType = WorkRequestType.DrFailback,
            Resources = new List<WorkRequestResource>
            {
                new()
                {
                    EntityType = "protectionGroup",
                    Identifier = request.ProtectionGroupId,
                    EntityName = request.ProtectionGroupId,
                    ActionType = "failback",
                },
            },
        });

        var workRequest = workRequestResponse.WorkRequest;

        // Update work request to InProgress
        _workRequestManager.UpdateWorkRequest(
            workRequest.Id,
            WorkRequestStatus.InProgress,
            10);

        lock (_lock)
        {
            if (!_protectionGroups.TryGetValue(request.ProtectionGroupId, out var group))
            {
                _workRequestManager.UpdateWorkRequest(
                    workRequest.Id,
                    WorkRequestStatus.Failed,
                    0,
                    new List<WorkRequestError>
                    {
                        new()
                        {
                            Code = "ProtectionGroupNotFound",
                            Message = $"Protection group '{request.ProtectionGroupId}' not found.",
                            Timestamp = DateTimeOffset.UtcNow,
                        },
                    });

                throw new KeyNotFoundException($"Protection group '{request.ProtectionGroupId}' not found.");
            }

            var activeRegion = group.Metadata.TryGetValue("ActiveRegion", out var ar) ? ar : string.Empty;
            if (activeRegion == group.PrimaryRegion())
            {
                _workRequestManager.UpdateWorkRequest(
                    workRequest.Id,
                    WorkRequestStatus.Failed,
                    0,
                    new List<WorkRequestError>
                    {
                        new()
                        {
                            Code = "InvalidState",
                            Message = "Cannot failback: already on primary region.",
                            Timestamp = DateTimeOffset.UtcNow,
                        },
                    });

                throw new InvalidOperationException("Cannot failback: already on primary region.");
            }

            group.Metadata["ActiveRegion"] = group.PrimaryRegion();
            group.Region = group.PrimaryRegion();
            group.UpdatedAt = DateTimeOffset.UtcNow;
            group.LifecycleState = DrResourceLifecycleState.Updating;
        }

        try
        {
            // Mark primary endpoints as healthy (30% progress)
            var primaryEndpoints = GetPrimaryEndpoints(request.ProtectionGroupId);
            foreach (var endpoint in primaryEndpoints)
            {
                await _loadBalancer.SetEndpointHealthAsync(endpoint, true, request.CancellationToken).ConfigureAwait(false);
            }

            _workRequestManager.UpdateWorkRequest(workRequest.Id, WorkRequestStatus.InProgress, 50);

            // Mark standby endpoints as unhealthy (80% progress)
            var standbyEndpoints = GetStandbyEndpoints(request.ProtectionGroupId);
            foreach (var endpoint in standbyEndpoints)
            {
                await _loadBalancer.SetEndpointHealthAsync(endpoint, false, request.CancellationToken).ConfigureAwait(false);
            }

            _workRequestManager.UpdateWorkRequest(workRequest.Id, WorkRequestStatus.InProgress, 90);

            lock (_lock)
            {
                _protectionGroups[request.ProtectionGroupId].LifecycleState = DrResourceLifecycleState.Active;
            }

            // Mark work request as succeeded
            _workRequestManager.UpdateWorkRequest(workRequest.Id, WorkRequestStatus.Succeeded, 100);

            var activeRegion = _protectionGroups[request.ProtectionGroupId].PrimaryRegion();

            return new FailbackResponse
            {
                OpcRequestId = request.RequestId ?? Guid.NewGuid().ToString("N"),
                Success = true,
                ActiveRegion = activeRegion,
            };
        }
        catch (Exception ex)
        {
            _workRequestManager.UpdateWorkRequest(
                workRequest.Id,
                WorkRequestStatus.Failed,
                0,
                new List<WorkRequestError>
                {
                    new()
                    {
                        Code = "FailbackFailed",
                        Message = ex.Message,
                        Timestamp = DateTimeOffset.UtcNow,
                    },
                });

            lock (_lock)
            {
                _protectionGroups[request.ProtectionGroupId].LifecycleState = DrResourceLifecycleState.Failed;
            }
            throw;
        }
    }

    /// <summary>
    /// Gets all protection groups.
    /// </summary>
    public IReadOnlyList<DrResourceInfo> GetProtectionGroups()
    {
        lock (_lock)
        {
            return _protectionGroups.Values.ToList().AsReadOnly();
        }
    }

    /// <summary>
    /// Gets a protection group by ID.
    /// </summary>
    public DrResourceInfo? GetProtectionGroup(string id)
    {
        lock (_lock)
        {
            return _protectionGroups.TryGetValue(id, out var group) ? group : null;
        }
    }

    /// <summary>
    /// Disposes the disaster recovery manager.
    /// </summary>
    public ValueTask DisposeAsync()
    {
        if (!_disposed)
        {
            _disposed = true;
            lock (_lock)
            {
                _protectionGroups.Clear();
            }
        }
        return ValueTask.CompletedTask;
    }

    private List<string> GetPrimaryEndpoints(string protectionGroupId)
    {
        lock (_lock)
        {
            if (!_protectionGroups.TryGetValue(protectionGroupId, out var group))
                return new List<string>();

            var endpointsStr = group.Metadata.TryGetValue("PrimaryEndpoints", out var ep) ? ep : string.Empty;
            return endpointsStr.Split(',', StringSplitOptions.RemoveEmptyEntries).ToList();
        }
    }

    private List<string> GetStandbyEndpoints(string protectionGroupId)
    {
        lock (_lock)
        {
            if (!_protectionGroups.TryGetValue(protectionGroupId, out var group))
                return new List<string>();

            var endpointsStr = group.Metadata.TryGetValue("StandbyEndpoints", out var ep) ? ep : string.Empty;
            return endpointsStr.Split(',', StringSplitOptions.RemoveEmptyEntries).ToList();
        }
    }
}

/// <summary>
/// Extension method to get the primary region from a protection group's metadata.
/// </summary>
public static class DrResourceInfoExtensions
{
    public static string PrimaryRegion(this DrResourceInfo group)
    {
        return group.Metadata.TryGetValue("PrimaryRegion", out var region) ? region : string.Empty;
    }
}
