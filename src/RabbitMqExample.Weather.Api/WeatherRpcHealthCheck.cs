using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace RabbitMqExample.Weather.Api;

/// <summary>
/// Publica como health check el estado que mantiene WeatherRpcWorker.
/// Si el arranque falló, incluye además el último error conocido.
/// </summary>
public sealed class WeatherRpcHealthCheck(WeatherRpcWorker worker) : IHealthCheck
{
    /// <summary>Comprueba conexión y canal sin consumir una petición real.</summary>
    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default
    ) =>
        Task.FromResult(
            worker.IsReady
                ? HealthCheckResult.Healthy("El consumidor RPC está conectado.")
                : HealthCheckResult.Unhealthy(
                    "El consumidor RPC no está conectado.",
                    // Los datos adicionales aparecerán en el informe de health checks.
                    data: worker.LastError is null
                        ? null
                        : new Dictionary<string, object> { ["lastError"] = worker.LastError }
                )
        );
}
