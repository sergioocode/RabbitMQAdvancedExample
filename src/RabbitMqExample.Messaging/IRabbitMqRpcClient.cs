namespace RabbitMqExample.Messaging;

/// <summary>
/// Abstracción que usa el Gateway para solicitar trabajo sin conocer los detalles
/// de conexiones, canales, colas temporales ni correlación de mensajes.
/// </summary>
public interface IRabbitMqRpcClient
{
    // Lo consulta el health check para informar si el cliente puede publicar.
    bool IsReady { get; }

    /// <summary>
    /// Publica una petición, espera la respuesta relacionada y la deserializa.
    /// </summary>
    Task<TResponse> CallAsync<TRequest, TResponse>(
        string operation,
        TRequest request,
        CancellationToken cancellationToken = default
    );
}
