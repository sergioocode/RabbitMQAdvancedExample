using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace RabbitMqExample.Messaging;

/// <summary>
/// Implementa manualmente el lado cliente del patrón RPC sobre RabbitMQ.
/// ASP.NET lo inicia como IHostedService, por lo que la conexión permanece abierta
/// y se reutiliza para todas las peticiones HTTP mientras el Gateway está activo.
/// </summary>
public sealed class RabbitMqRpcClient : IRabbitMqRpcClient, IHostedService, IAsyncDisposable
{
    private readonly RabbitMqOptions _options;
    private readonly ILogger<RabbitMqRpcClient> _logger;

    // Relaciona cada CorrelationId con la petición HTTP que espera su respuesta.
    // ConcurrentDictionary permite tener varias llamadas RPC en curso a la vez.
    private readonly ConcurrentDictionary<string, TaskCompletionSource<byte[]>> _pending = new();

    // Protege el arranque/cierre y evita publicaciones simultáneas sobre el canal.
    private readonly SemaphoreSlim _lifecycleLock = new(1, 1);
    private readonly SemaphoreSlim _publishLock = new(1, 1);

    // Una conexión puede alojar varios canales. Este ejemplo usa un canal compartido.
    private IConnection? _connection;
    private IChannel? _channel;

    // RabbitMQ genera este nombre (amq.gen-...) al declarar una cola sin nombre.
    private string? _replyQueueName;

    public RabbitMqRpcClient(IOptions<RabbitMqOptions> options, ILogger<RabbitMqRpcClient> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    // El health check usa esta propiedad para distinguir proceso arrancado de
    // cliente realmente conectado y preparado para recibir respuestas.
    public bool IsReady =>
        _connection?.IsOpen == true
        && _channel?.IsOpen == true
        && !string.IsNullOrWhiteSpace(_replyQueueName);

    /// <summary>Abre la conexión y prepara la topología necesaria para el RPC.</summary>
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await _lifecycleLock.WaitAsync(cancellationToken);
        try
        {
            // IHostedService no debería iniciarse dos veces, pero esta comprobación
            // hace que el método sea seguro si se vuelve a invocar.
            if (IsReady)
            {
                return;
            }

            // ConnectionFactory contiene los datos del broker y habilita la
            // recuperación automática frente a cortes breves de red.
            ConnectionFactory factory = new()
            {
                HostName = _options.HostName,
                Port = _options.Port,
                UserName = _options.UserName,
                Password = _options.Password,
                VirtualHost = _options.VirtualHost,
                AutomaticRecoveryEnabled = true,
                TopologyRecoveryEnabled = true,
                NetworkRecoveryInterval = TimeSpan.FromSeconds(5),
            };

            _connection = await factory.CreateConnectionAsync(cancellationToken);

            // Los publisher confirms permiten que la publicación espere la
            // confirmación del broker en lugar de asumir que el mensaje llegó.
            _channel = await _connection.CreateChannelAsync(
                new CreateChannelOptions(
                    publisherConfirmationsEnabled: true,
                    publisherConfirmationTrackingEnabled: true
                ),
                cancellationToken
            );

            // Cola de solicitudes compartida con WeatherRpcWorker. Es duradera
            // y no desaparece al cerrar ninguna de las dos aplicaciones.
            await _channel.QueueDeclareAsync(
                queue: _options.RequestQueue,
                durable: true,
                exclusive: false,
                autoDelete: false,
                arguments: null,
                cancellationToken: cancellationToken
            );

            // Cola privada de respuestas: RabbitMQ genera amq.gen-..., pertenece
            // a esta conexión y se elimina automáticamente cuando esta se cierra.
            QueueDeclareOk replyQueue = await _channel.QueueDeclareAsync(
                queue: string.Empty,
                durable: false,
                exclusive: true,
                autoDelete: true,
                arguments: null,
                cancellationToken: cancellationToken
            );

            _replyQueueName = replyQueue.QueueName;

            AsyncEventingBasicConsumer consumer = new(_channel);
            consumer.ReceivedAsync += HandleReplyAsync;

            // autoAck es suficiente aquí: al recibir una respuesta se copia en
            // memoria y se completa inmediatamente la tarea que estaba esperando.
            await _channel.BasicConsumeAsync(
                queue: _replyQueueName,
                autoAck: true,
                consumer: consumer,
                cancellationToken: cancellationToken
            );

            _logger.LogInformation(
                "Cliente RPC conectado a RabbitMQ. Cola de respuesta: {ReplyQueue}",
                _replyQueueName
            );
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    /// <summary>
    /// Ejecuta una llamada RPC completa: publica, espera, correlaciona y deserializa.
    /// </summary>
    public async Task<TResponse> CallAsync<TRequest, TResponse>(
        string operation,
        TRequest request,
        CancellationToken cancellationToken = default
    )
    {
        // Paso 1: una petición no puede publicarse hasta que StartAsync haya
        // creado la conexión, el canal y la cola temporal de respuesta.
        if (!IsReady || _channel is null || _replyQueueName is null)
        {
            throw new InvalidOperationException(
                "El cliente RPC todavía no está conectado a RabbitMQ."
            );
        }

        // Paso 2: el identificador único permitirá reconocer la respuesta aunque
        // varias solicitudes HTTP estén usando la misma cola temporal.
        string correlationId = Guid.NewGuid().ToString("N");
        TaskCompletionSource<byte[]> completion = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );

        if (!_pending.TryAdd(correlationId, completion))
        {
            throw new InvalidOperationException("No se pudo registrar la petición RPC.");
        }

        // Paso 3: combina la cancelación HTTP con el límite configurado para no
        // dejar una llamada esperando indefinidamente si Weather no responde.
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken
        );
        timeout.CancelAfter(TimeSpan.FromSeconds(_options.RequestTimeoutSeconds));
        using CancellationTokenRegistration registration = timeout.Token.Register(() =>
            completion.TrySetCanceled(timeout.Token)
        );

        try
        {
            // Paso 4: envuelve el contrato concreto en RpcRequestMessage y lo
            // serializa a los bytes JSON que RabbitMQ transportará.
            RpcRequestMessage message = new(
                operation,
                JsonSerializer.SerializeToElement(request, RpcJson.Options)
            );
            byte[] body = JsonSerializer.SerializeToUtf8Bytes(message, RpcJson.Options);
            BasicProperties properties = new()
            {
                ContentType = "application/json",
                CorrelationId = correlationId,

                // ReplyTo indica al consumidor en qué cola publicar la respuesta.
                ReplyTo = _replyQueueName,

                // La petición queda marcada como persistente porque rpc.weather
                // también es una cola duradera.
                Persistent = true,
            };

            _logger.LogInformation(
                "Publicando petición RPC. Operación: {Operation}; Cola: {RequestQueue}; "
                    + "CorrelationId: {CorrelationId}; ReplyTo: {ReplyQueue}.",
                operation,
                _options.RequestQueue,
                correlationId,
                _replyQueueName
            );

            // Paso 5: IChannel no debe publicar concurrentemente; el semáforo
            // serializa solo esta sección sin bloquear la espera de respuestas.
            await _publishLock.WaitAsync(timeout.Token);
            try
            {
                // Exchange vacío = default exchange. RabbitMQ enruta directamente
                // a la cola cuyo nombre coincide con routingKey (rpc.weather).
                await _channel.BasicPublishAsync(
                    exchange: string.Empty,
                    routingKey: _options.RequestQueue,
                    mandatory: true,
                    basicProperties: properties,
                    body: body,
                    cancellationToken: timeout.Token
                );

                _logger.LogInformation(
                    "RabbitMQ confirmó la publicación. CorrelationId: {CorrelationId}.",
                    correlationId
                );
            }
            finally
            {
                _publishLock.Release();
            }

            // Paso 6: queda suspendido sin bloquear un hilo. HandleReplyAsync
            // completará esta tarea cuando llegue el mismo CorrelationId.
            byte[] responseBody = await completion.Task;

            _logger.LogInformation(
                "Respuesta RPC recibida. CorrelationId: {CorrelationId}; Bytes: {ByteCount}.",
                correlationId,
                responseBody.Length
            );

            // Paso 7: reconstruye el sobre de respuesta y traduce los errores
            // remotos a una excepción comprensible para el endpoint HTTP.
            RpcResponseMessage response =
                JsonSerializer.Deserialize<RpcResponseMessage>(responseBody, RpcJson.Options)
                ?? throw new JsonException("La respuesta RPC está vacía.");

            return !response.IsSuccess
                    ? throw new RemoteRpcException(
                        response.Error ?? "El servicio remoto devolvió un error."
                    )
                : response.Payload is null
                    ? throw new JsonException("La respuesta RPC no contiene datos.")
                : response.Payload.Value.Deserialize<TResponse>(RpcJson.Options)
                    ?? throw new JsonException("No se pudo deserializar la respuesta RPC.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Si el cliente HTTP no canceló, la cancelación procede del timeout RPC.
            throw new TimeoutException(
                $"El servicio remoto no respondió en {_options.RequestTimeoutSeconds} segundos."
            );
        }
        finally
        {
            // La entrada debe retirarse tanto en éxito como en error o cancelación.
            _pending.TryRemove(correlationId, out _);
        }
    }

    /// <summary>Finaliza las esperas pendientes y cierra los recursos del broker.</summary>
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        foreach (TaskCompletionSource<byte[]> completion in _pending.Values)
        {
            completion.TrySetException(
                new InvalidOperationException("El cliente RPC se está deteniendo.")
            );
        }

        _pending.Clear();
        await DisposeBrokerResourcesAsync();
    }

    // RabbitMQ invoca este manejador por cada mensaje de la cola amq.gen-...
    private Task HandleReplyAsync(object sender, BasicDeliverEventArgs eventArgs)
    {
        string? correlationId = eventArgs.BasicProperties.CorrelationId;

        // Solo se completa la llamada que registró exactamente este identificador.
        if (
            correlationId is not null
            && _pending.TryGetValue(correlationId, out TaskCompletionSource<byte[]>? completion)
        )
        {
            completion.TrySetResult(eventArgs.Body.ToArray());
        }

        return Task.CompletedTask;
    }

    // El canal se cierra antes que la conexión que lo contiene.
    private async Task DisposeBrokerResourcesAsync()
    {
        if (_channel is not null)
        {
            await _channel.DisposeAsync();
            _channel = null;
        }

        if (_connection is not null)
        {
            await _connection.DisposeAsync();
            _connection = null;
        }

        _replyQueueName = null;
    }

    // IAsyncDisposable cubre también cierres fuera del ciclo normal del host.
    public async ValueTask DisposeAsync()
    {
        await DisposeBrokerResourcesAsync();
        _lifecycleLock.Dispose();
        _publishLock.Dispose();
    }
}
