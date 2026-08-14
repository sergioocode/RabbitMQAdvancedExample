namespace RabbitMqExample.Contracts;

/// <summary>
/// Resultado que Weather devuelve y que el Gateway expone finalmente por HTTP.
/// Al estar en Contracts, ambos servicios serializan exactamente la misma estructura.
/// </summary>
public sealed record WeatherForecast(DateOnly Date, int TemperatureC, string Summary)
{
    // Propiedad calculada: no necesita almacenarse en el constructor del contrato.
    public int TemperatureF => 32 + (int)(TemperatureC / 0.5556);
}
