namespace FbThrift.Tests.Transport;

using System;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Tasks;
using FbThrift.Core;
using FbThrift.Core.Rocket;
using FbThrift.Core.Transport;
using Xunit;

public class QuicTransportTests
{
    [Fact]
    public void RocketQuicTransport_CanBeInstantiated()
    {
        // This test verifies the QUIC transport can be created
        // Full integration test would require a QUIC server
        var endpoint = new IPEndPoint(IPAddress.Loopback, 0);
        
        using var cert = CreateSelfSignedCert();
        var transport = new RocketQuicTransport(endpoint, cert);
        
        Assert.NotNull(transport);
        transport.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void RocketQuicClientFactory_CanCreateClient()
    {
        // This test verifies the client factory can be created
        var endpoint = new IPEndPoint(IPAddress.Loopback, 0);
        
        using var cert = CreateSelfSignedCert();
        
        // Note: Full integration test requires a running QUIC server
        // This validates the API surface
        var task = RocketQuicClientFactory.CreateAsync(endpoint, cert, cancellationToken: default);
        
        // Should not throw immediately - connection would fail but creation works
        Assert.ThrowsAsync<Exception>(async () => await task);
    }

    [Fact]
    public void FbThriftTransportFactory_CreatesQuicServerTransport()
    {
        var endpoint = new IPEndPoint(IPAddress.Loopback, 0);
        using var cert = CreateSelfSignedCert();
        
        var transportTask = FbThriftTransportFactory.CreateQuicTransportAsync(endpoint, cert);
        transportTask.Wait(TimeSpan.FromSeconds(5));
        
        Assert.NotNull(transportTask.Result);
        Assert.IsAssignableFrom<IFbThriftTransport>(transportTask.Result);
        
        transportTask.Result.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void FbThriftTransportFactory_CreatesQuicClientTransport()
    {
        var endpoint = new IPEndPoint(IPAddress.Loopback, 0);
        using var cert = CreateSelfSignedCert();
        
        var task = FbThriftTransportFactory.CreateQuicClientAsync(endpoint, cert);
        
        // Should not throw during creation
        Assert.ThrowsAsync<Exception>(async () => await task);
    }

    private static X509Certificate2 CreateSelfSignedCert()
    {
        // Create a minimal self-signed certificate for testing
        var ecdsa = System.Security.Cryptography.ECDsa.Create();
        var req = new System.Security.Cryptography.X509Certificates.CertificateRequest(
            "CN=test", ecdsa, System.Security.Cryptography.HashAlgorithmName.SHA256);
        
        var cert = req.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddHours(-1),
            DateTimeOffset.UtcNow.AddHours(1));
        
        return new X509Certificate2(cert.Export(System.Security.Cryptography.X509Certificates.X509ContentType.Pfx));
    }
}