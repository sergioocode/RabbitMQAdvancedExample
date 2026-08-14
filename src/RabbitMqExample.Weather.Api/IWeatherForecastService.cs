using RabbitMqExample.Contracts;

namespace RabbitMqExample.Weather.Api;

/// <summary>
/// Lógica de generación independiente del transporte. El consumidor depende de
/// esta abstracción y no necesita saber cómo se construyen los pronósticos.
/// </summary>
public interface IWeatherForecastService
{
    /// <summary>Genera un pronóstico por cada día solicitado.</summary>
    WeatherForecast[] Create(int days);
}
