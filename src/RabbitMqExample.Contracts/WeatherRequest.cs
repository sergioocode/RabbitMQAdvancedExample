namespace RabbitMqExample.Contracts;

/// <summary>
/// Datos que el Gateway envía al consumidor para solicitar un número de pronósticos.
/// Este contrato se comparte entre ambos servicios, pero no contiene lógica de RabbitMQ.
/// </summary>
public sealed record WeatherRequest(int Days);
