using System.ComponentModel.DataAnnotations;

namespace RabbitMqExample.Messaging;

/// <summary>
/// Configuración común que utilizan el Gateway y el servicio Weather para
/// conectarse al mismo broker y trabajar con la misma cola de peticiones.
/// Las anotaciones permiten detectar valores inválidos al arrancar la aplicación.
/// </summary>
public sealed class RabbitMqOptions
{
    // Nombre de la sección que se enlaza desde appsettings.json.
    public const string SectionName = "RabbitMq";

    // Dirección del broker: localhost fuera de Docker y rabbitmq dentro de Compose.
    [Required]
    public string HostName { get; init; } = "localhost";

    // Puerto del protocolo AMQP; no es el puerto 15672 del panel web.
    [Range(1, 65_535)]
    public int Port { get; init; } = 5672;

    // Credenciales creadas mediante las variables de entorno de compose.yaml.
    [Required]
    public string UserName { get; init; } = "app";

    [Required]
    public string Password { get; init; } = "app";

    [Required]
    public string VirtualHost { get; init; } = "/";

    // Cola duradera en la que el Gateway publica las solicitudes RPC.
    [Required]
    public string RequestQueue { get; init; } = "rpc.weather";

    // Tiempo máximo que una petición HTTP esperará la respuesta del consumidor.
    [Range(1, 300)]
    public int RequestTimeoutSeconds { get; init; } = 10;
}
