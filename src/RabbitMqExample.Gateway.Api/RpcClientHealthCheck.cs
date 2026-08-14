using Microsoft.Extensions.Diagnostics.HealthChecks;
using RabbitMqExample.Messaging;

namespace RabbitMqExample.Gateway.Api;

/// <summary>
/// Adapta el estado del cliente RPC al sistema estándar de health checks de ASP.NET.
/// Así /health será Unhealthy si la API está viva pero RabbitMQ no está conectado.
/// </summary>
public sealed class RpcClientHealthCheck(IRabbitMqRpcClient client) : IHealthCheck
{
    /// <summary>Consulta inmediata: no publica ningún mensaje de prueba.</summary>
    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default
    ) =>
        Task.FromResult(
            client.IsReady
                ? HealthCheckResult.Healthy("El cliente RPC está conectado.")
                : HealthCheckResult.Unhealthy("El cliente RPC no está conectado.")
        );
}
