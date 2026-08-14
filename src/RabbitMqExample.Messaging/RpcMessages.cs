using System.Text.Json;

namespace RabbitMqExample.Messaging;

/// <summary>
/// Sobre común enviado por el Gateway. Operation indica qué acción debe ejecutar
/// el consumidor y Payload contiene los datos concretos de esa acción.
/// </summary>
public sealed record RpcRequestMessage(string Operation, JsonElement Payload);

/// <summary>
/// Sobre común de respuesta. Permite devolver datos o trasladar un error remoto
/// sin depender del tipo concreto de la operación.
/// </summary>
public sealed record RpcResponseMessage(bool IsSuccess, JsonElement? Payload, string? Error)
{
    // Convierte cualquier resultado correcto a JsonElement para transportarlo.
    public static RpcResponseMessage Ok<T>(T value) =>
        new(true, JsonSerializer.SerializeToElement(value, RpcJson.Options), null);

    // Los errores viajan como datos y el cliente los transforma en una excepción.
    public static RpcResponseMessage Fail(string error) => new(false, null, error);
}

/// <summary>Representa un error devuelto por el microservicio remoto.</summary>
public sealed class RemoteRpcException(string message) : Exception(message);

/// <summary>Opciones JSON compartidas por publicador y consumidor.</summary>
public static class RpcJson
{
    // JsonSerializerDefaults.Web aplica camelCase y las convenciones JSON de ASP.NET.
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web);
}
