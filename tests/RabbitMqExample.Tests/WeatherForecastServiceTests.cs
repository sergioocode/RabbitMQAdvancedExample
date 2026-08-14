using RabbitMqExample.Contracts;
using RabbitMqExample.Weather.Api;

namespace RabbitMqExample.Tests;

/// <summary>
/// Pruebas unitarias de la lógica de negocio. No arrancan Docker ni RabbitMQ:
/// demuestran que WeatherForecastService está desacoplado de la mensajería.
/// </summary>
public sealed class WeatherForecastServiceTests
{
    // Una fecha fija evita que el resultado dependa del día en que se ejecute el test.
    private static readonly DateTimeOffset Now = new(2026, 8, 11, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Create_ReturnsRequestedNumberOfConsecutiveDays()
    {
        // Arrange: servicio con un reloj controlado.
        WeatherForecastService service = new(new FixedTimeProvider(Now));

        // Act: se solicitan cinco días.
        WeatherForecast[] forecasts = service.Create(5);

        // Assert: cantidad, fechas consecutivas y rango de temperatura válidos.
        Assert.Equal(5, forecasts.Length);
        Assert.Equal(new DateOnly(2026, 8, 12), forecasts[0].Date);
        Assert.Equal(new DateOnly(2026, 8, 16), forecasts[^1].Date);
        Assert.All(forecasts, forecast => Assert.InRange(forecast.TemperatureC, -20, 55));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(15)]
    public void Create_RejectsUnsupportedRange(int days)
    {
        WeatherForecastService service = new(new FixedTimeProvider(Now));

        // Cero y quince representan los dos límites inválidos de la regla 1..14.
        Assert.Throws<ArgumentOutOfRangeException>(() => service.Create(days));
    }

    // Sustituye TimeProvider.System únicamente dentro de estas pruebas.
    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
