// Copyright (c) 2024 FbThrift Contributors. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace FbThrift.LoadBalancing;

using System;
using System.Collections.Generic;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// Certificate lifecycle states, following OCI SDK Certificates patterns.
/// </summary>
public enum CertificateLifecycleState
{
    Creating,
    Active,
    Updating,
    Deleting,
    Deleted,
    Failed,
    PendingRotation,
}

/// <summary>
/// Certificate bundle type, following OCI SDK patterns.
/// </summary>
public enum CertificateBundleType
{
    /// <summary>
    /// Certificate only (no private key).
    /// </summary>
    CertificateOnly,

    /// <summary>
    /// Certificate with private key.
    /// </summary>
    WithPrivateKey,
}

/// <summary>
/// Information about a certificate.
/// </summary>
public sealed class CertificateInfo
{
    /// <summary>
    /// Gets the certificate ID.
    /// </summary>
    public string Id { get; init; } = string.Empty;

    /// <summary>
    /// Gets or sets the certificate name.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the certificate description.
    /// </summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>
    /// Gets the current lifecycle state.
    /// </summary>
    public CertificateLifecycleState LifecycleState { get; set; } = CertificateLifecycleState.Creating;

    /// <summary>
    /// Gets or sets the X.509 certificate.
    /// </summary>
    public X509Certificate2? Certificate { get; set; }

    /// <summary>
    /// Gets or sets the certificate version.
    /// </summary>
    public string VersionNumber { get; set; } = "1";

    /// <summary>
    /// Gets or sets the certificate stages (e.g., "CURRENT", "PENDING", "DEPRECATED").
    /// </summary>
    public List<string> Stages { get; set; } = new() { "CURRENT" };

    /// <summary>
    /// Gets or sets the timestamp when the certificate was created.
    /// </summary>
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Gets or sets the timestamp when the certificate expires.
    /// </summary>
    public DateTimeOffset? ExpiresAt { get; set; }

    /// <summary>
    /// Gets or sets arbitrary metadata.
    /// </summary>
    public Dictionary<string, string> Metadata { get; } = new();

    /// <summary>
    /// Gets whether the certificate is expired.
    /// </summary>
    public bool IsExpired => ExpiresAt.HasValue && ExpiresAt.Value <= DateTimeOffset.UtcNow;

    /// <summary>
    /// Gets whether the certificate is expiring soon (within 30 days).
    /// </summary>
    public bool IsExpiringSoon => ExpiresAt.HasValue && ExpiresAt.Value <= DateTimeOffset.UtcNow.AddDays(30);
}

/// <summary>
/// Request to get a certificate bundle.
/// </summary>
public sealed class GetCertificateBundleRequest
{
    /// <summary>
    /// Gets or sets the certificate ID.
    /// </summary>
    public string CertificateId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the bundle type (certificate only or with private key).
    /// </summary>
    public CertificateBundleType BundleType { get; set; } = CertificateBundleType.CertificateOnly;

    /// <summary>
    /// Gets or sets the certificate version (null for CURRENT).
    /// </summary>
    public string? VersionNumber { get; set; }

    /// <summary>
    /// Gets or sets the certificate stage (e.g., "CURRENT", "PENDING").
    /// </summary>
    public string? Stage { get; set; }

    /// <summary>
    /// Gets or sets the cancellation token.
    /// </summary>
    public CancellationToken CancellationToken { get; set; }
}

/// <summary>
/// Response for getting a certificate bundle.
/// </summary>
public sealed class GetCertificateBundleResponse
{
    /// <summary>
    /// Gets or sets the certificate info.
    /// </summary>
    public CertificateInfo? Certificate { get; set; }

    /// <summary>
    /// Gets or sets the certificate bundle data (PEM encoded).
    /// </summary>
    public byte[]? BundleData { get; set; }

    /// <summary>
    /// Gets or sets the private key data (PEM encoded, only if BundleType is WithPrivateKey).
    /// </summary>
    public byte[]? PrivateKeyData { get; set; }

    /// <summary>
    /// Gets or sets the certificate version.
    /// </summary>
    public string VersionNumber { get; set; } = string.Empty;
}

/// <summary>
/// Request to create a certificate.
/// </summary>
public sealed class CreateCertificateRequest
{
    /// <summary>
    /// Gets or sets the certificate name.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the certificate description.
    /// </summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the PEM-encoded certificate data.
    /// </summary>
    public string CertificatePem { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the PEM-encoded private key data.
    /// </summary>
    public string? PrivateKeyPem { get; set; }

    /// <summary>
    /// Gets or sets the certificate expiry date.
    /// </summary>
    public DateTimeOffset? ExpiresAt { get; set; }

    /// <summary>
    /// Gets or sets arbitrary metadata.
    /// </summary>
    public Dictionary<string, string> Metadata { get; } = new();
}

/// <summary>
/// Response for creating a certificate.
/// </summary>
public sealed class CreateCertificateResponse
{
    /// <summary>
    /// Gets or sets the created certificate info.
    /// </summary>
    public CertificateInfo? Certificate { get; set; }

    /// <summary>
    /// Gets or sets the unique identifier for the response.
    /// </summary>
    public string? OpcRequestId { get; set; }
}

/// <summary>
/// Certificate manager for fbthrift, following OCI SDK CertificatesClient pattern.
/// Manages TLS certificate lifecycle including rotation and staging.
/// </summary>
public sealed class CertificateManager : IAsyncDisposable
{
    private readonly Dictionary<string, CertificateInfo> _certificates = new(StringComparer.OrdinalIgnoreCase);
    private readonly WorkRequestManager _workRequestManager;
    private readonly object _lock = new();

    /// <summary>
    /// Initializes a new instance of <see cref="CertificateManager"/>.
    /// </summary>
    /// <param name="workRequestManager">The work request manager for tracking async operations.</param>
    public CertificateManager(WorkRequestManager? workRequestManager = null)
    {
        _workRequestManager = workRequestManager ?? new WorkRequestManager();
    }
    private bool _disposed;

    /// <summary>
    /// Creates a new certificate.
    /// </summary>
    public CreateCertificateResponse CreateCertificate(CreateCertificateRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var cert = new CertificateInfo
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = request.Name,
            Description = request.Description,
            ExpiresAt = request.ExpiresAt,
            LifecycleState = CertificateLifecycleState.Active,
        };
        foreach (var kvp in request.Metadata)
            cert.Metadata[kvp.Key] = kvp.Value;

        if (!string.IsNullOrWhiteSpace(request.CertificatePem))
        {
            try
            {
                cert.Certificate = new X509Certificate2(
                    System.Text.Encoding.UTF8.GetBytes(request.CertificatePem));
            }
            catch
            {
                // If PEM parsing fails, store as-is
            }
        }

        lock (_lock)
        {
            _certificates[cert.Id] = cert;
        }

        return new CreateCertificateResponse
        {
            OpcRequestId = Guid.NewGuid().ToString("N"),
            Certificate = cert,
        };
    }

    /// <summary>
    /// Gets a certificate bundle.
    /// </summary>
    public ValueTask<GetCertificateBundleResponse> GetCertificateBundleAsync(
        GetCertificateBundleRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        CertificateInfo? cert;
        lock (_lock)
        {
            cert = _certificates.TryGetValue(request.CertificateId, out var c) ? c : null;
        }

        if (cert == null)
        {
            return ValueTask.FromResult(new GetCertificateBundleResponse
            {
                Certificate = null,
                VersionNumber = string.Empty,
            });
        }

        // Determine which version/stage to return
        var version = request.VersionNumber ?? cert.VersionNumber;
        var stage = request.Stage ?? "CURRENT";

        if (!cert.Stages.Contains(stage) && stage != "CURRENT")
        {
            return ValueTask.FromResult(new GetCertificateBundleResponse
            {
                Certificate = null,
                VersionNumber = version,
            });
        }

        byte[]? bundleData = null;
        byte[]? privateKeyData = null;

        if (cert.Certificate != null)
        {
            bundleData = cert.Certificate.Export(X509ContentType.Cert);
        }

        if (request.BundleType == CertificateBundleType.WithPrivateKey && cert.Certificate != null)
        {
            try
            {
                privateKeyData = cert.Certificate.Export(X509ContentType.Pkcs12, "");
            }
            catch
            {
                // Private key not exportable
            }
        }

        return ValueTask.FromResult(new GetCertificateBundleResponse
        {
            Certificate = cert,
            BundleData = bundleData,
            PrivateKeyData = privateKeyData,
            VersionNumber = version,
        });
    }

    /// <summary>
    /// Rotates a certificate by adding a new version and marking the old one as deprecated.
    /// Returns a WorkRequest that clients can poll using the Waiter pattern.
    /// </summary>
    public async Task<CertificateInfo> RotateCertificateAsync(
        string certificateId,
        string newCertificatePem,
        string? newPrivateKeyPem = null,
        CancellationToken cancellationToken = default)
    {
        CertificateInfo? oldCert;
        lock (_lock)
        {
            if (!_certificates.TryGetValue(certificateId, out oldCert))
                throw new KeyNotFoundException($"Certificate '{certificateId}' not found.");

            // Mark old certificate as DEPRECATED
            oldCert.Stages = new List<string> { "DEPRECATED" };
        }

        // Create work request for tracking
        var workRequestResponse = _workRequestManager.CreateWorkRequest(new CreateWorkRequestRequest
        {
            OperationType = WorkRequestType.CertificateRotation,
            Resources = new List<WorkRequestResource>
            {
                new()
                {
                    EntityType = "certificate",
                    Identifier = certificateId,
                    EntityName = oldCert.Name,
                    ActionType = "rotate",
                },
            },
        });

        var workRequest = workRequestResponse.WorkRequest;

        _workRequestManager.UpdateWorkRequest(
            workRequest.Id,
            WorkRequestStatus.InProgress,
            30);

        try
        {
            // Create new certificate
            var newCert = new CertificateInfo
            {
                Id = certificateId, // Same ID, new version
                Name = oldCert.Name,
                Description = oldCert.Description,
                ExpiresAt = DateTimeOffset.UtcNow.AddYears(1),
                LifecycleState = CertificateLifecycleState.Active,
                Stages = new List<string> { "CURRENT", "PENDING" },
            };
            foreach (var kvp in oldCert.Metadata)
                newCert.Metadata[kvp.Key] = kvp.Value;

            try
            {
                newCert.Certificate = new X509Certificate2(
                    System.Text.Encoding.UTF8.GetBytes(newCertificatePem));
            }
            catch
            {
                // If PEM parsing fails, store as-is
            }

            _workRequestManager.UpdateWorkRequest(workRequest.Id, WorkRequestStatus.InProgress, 70);

            lock (_lock)
            {
                _certificates[certificateId] = newCert;
            }

            _workRequestManager.UpdateWorkRequest(workRequest.Id, WorkRequestStatus.Succeeded, 100);

            return newCert;
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
                        Code = "CertificateRotationFailed",
                        Message = ex.Message,
                        Timestamp = DateTimeOffset.UtcNow,
                    },
                });

            throw;
        }
    }

    /// <summary>
    /// Gets all certificates.
    /// </summary>
    public IReadOnlyList<CertificateInfo> GetCertificates()
    {
        lock (_lock)
        {
            return _certificates.Values.ToList().AsReadOnly();
        }
    }

    /// <summary>
    /// Gets certificates that are expiring soon (within 30 days).
    /// </summary>
    public IReadOnlyList<CertificateInfo> GetExpiringCertificates()
    {
        lock (_lock)
        {
            return _certificates.Values
                .Where(c => c.IsExpiringSoon && !c.IsExpired)
                .ToList()
                .AsReadOnly();
        }
    }

    /// <summary>
    /// Deletes a certificate.
    /// </summary>
    public bool DeleteCertificate(string certificateId)
    {
        lock (_lock)
        {
            return _certificates.Remove(certificateId);
        }
    }

    /// <summary>
    /// Disposes the certificate manager.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (!_disposed)
        {
            _disposed = true;
            lock (_lock)
            {
                foreach (var cert in _certificates.Values)
                {
                    cert.Certificate?.Dispose();
                }
                _certificates.Clear();
            }
            await _workRequestManager.DisposeAsync().ConfigureAwait(false);
        }
    }
}
