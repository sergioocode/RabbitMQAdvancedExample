using RabbitMqExample.Contracts;

namespace RabbitMqExample.Weather.Api;

/// <summary>
/// Lógica de negocio del ejemplo. No contiene referencias a RabbitMQ ni a HTTP,
/// por eso puede probarse de forma aislada y rápida.
/// </summary>
public sealed class WeatherForecastService(TimeProvider timeProvider) : IWeatherForecastService
{
    private static readonly string[] Summaries =
    [
        "Freezing",
        "Bracing",
        "Chilly",
        "Cool",
        "Mild",
        "Warm",
        "Balmy",
        "Hot",
        "Sweltering",
        "Scorching",
    ];

    /// <summary>Genera entre uno y catorce pronósticos consecutivos.</summary>
    public WeatherForecast[] Create(int days)
    {
        // La validación se conserva en el consumidor aunque el Gateway también
        // valide: nunca se debe confiar únicamente en otro proceso.
        if (days is < 1 or > 14)
        {
            throw new ArgumentOutOfRangeException(
                nameof(days),
                "El número de días debe estar entre 1 y 14."
            );
        }

        // TimeProvider permite usar el reloj real en producción y uno fijo en tests.
        DateOnly today = DateOnly.FromDateTime(timeProvider.GetUtcNow().UtcDateTime);

        // Empieza mañana y crea exactamente la cantidad solicitada.
        return Enumerable
            .Range(1, days)
            .Select(index => new WeatherForecast(
                today.AddDays(index),
                Random.Shared.Next(-20, 56),
                Summaries[Random.Shared.Next(Summaries.Length)]
            ))
            .ToArray();
    }
}
