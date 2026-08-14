using System.Text.Json;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using RabbitMqExample.Contracts;
using RabbitMqExample.Messaging;

namespace RabbitMqExample.Weather.Api;

/// <summary>
/// Lado consumidor del RPC. BackgroundService lo mantiene escuchando la cola
/// rpc.weather durante toda la vida de la aplicación y responde a ReplyTo.
/// </summary>
public sealed class WeatherRpcWorker : BackgroundService
{
    private readonly RabbitMqOptions _options;
    private readonly IWeatherForecastService _weatherService;
    private readonly ILogger<WeatherRpcWorker> _logger;

    private IConnection? _connection;
    private IChannel? _channel;

    public WeatherRpcWorker(
        IOptions<RabbitMqOptions> options,
        IWeatherForecastService weatherService,
        ILogger<WeatherRpcWorker> logger
    )
    {
        _options = options.Value;
        _weatherService = weatherService;
        _logger = logger;
    }

    // El health check considera preparado al consumidor cuando ambos recursos
    // AMQP están abiertos.
    public bool IsReady => _connection?.IsOpen == true && _channel?.IsOpen == true;

    // Conserva el último fallo de conexión para mostrarlo mediante /health.
    public string? LastError { get; private set; }

    /// <summary>Conecta el consumidor y reintenta si el arranque falla.</summary>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ConsumeAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // Cancelación normal cuando se detiene la aplicación.
                break;
            }
            catch (Exception exception)
            {
                // RabbitMQ puede no estar disponible todavía. Se limpian los
                // recursos parciales y se vuelve a intentar cinco segundos después.
                LastError = exception.Message;
                _logger.LogError(
                    exception,
                    "No se pudo iniciar el consumidor RPC. Se reintentará."
                );
                await DisposeBrokerResourcesAsync();
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }
    }

    // Prepara la conexión, declara la cola y registra el manejador de mensajes.
    private async Task ConsumeAsync(CancellationToken cancellationToken)
    {
        // AutomaticRecovery reconstruye la conexión y la topología frente a
        // interrupciones de red que ocurran después de un arranque correcto.
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

        // Las confirmaciones permiten saber que las respuestas publicadas fueron
        // aceptadas por el broker antes de confirmar la petición original.
        _channel = await _connection.CreateChannelAsync(
            new CreateChannelOptions(
                publisherConfirmationsEnabled: true,
                publisherConfirmationTrackingEnabled: true
            ),
            cancellationToken
        );

        // Debe coincidir con la declaración del cliente: mismo nombre y mismas
        // propiedades. RabbitMQ rechaza declaraciones incompatibles.
        await _channel.QueueDeclareAsync(
            queue: _options.RequestQueue,
            durable: true,
            exclusive: false,
            autoDelete: false,
            arguments: null,
            cancellationToken: cancellationToken
        );

        // Entrega como máximo una petición sin confirmar. Hasta enviar su ACK,
        // este consumidor no recibe la siguiente, evitando acumular trabajo.
        await _channel.BasicQosAsync(
            prefetchSize: 0,
            prefetchCount: 1,
            global: false,
            cancellationToken: cancellationToken
        );

        AsyncEventingBasicConsumer consumer = new(_channel);
        consumer.ReceivedAsync += HandleRequestAsync;

        // autoAck:false obliga a confirmar cada mensaje explícitamente después
        // de procesarlo; si el proceso cae antes, RabbitMQ podrá volver a entregarlo.
        await _channel.BasicConsumeAsync(
            queue: _options.RequestQueue,
            autoAck: false,
            consumer: consumer,
            cancellationToken: cancellationToken
        );

        LastError = null;
        _logger.LogInformation(
            "Consumidor RPC escuchando la cola {RequestQueue}.",
            _options.RequestQueue
        );

        // Mantiene vivo el BackgroundService. El manejador se ejecuta cada vez
        // que AsyncEventingBasicConsumer recibe una petición.
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
    }

    // Paso 4 del flujo: RabbitMQ entrega aquí una solicitud publicada por Gateway.
    private async Task HandleRequestAsync(object sender, BasicDeliverEventArgs eventArgs)
    {
        if (_channel is null)
        {
            return;
        }

        byte[] body = eventArgs.Body.ToArray();
        RpcResponseMessage response;

        try
        {
            // Reconstruye primero el sobre genérico con operación y payload.
            RpcRequestMessage request =
                JsonSerializer.Deserialize<RpcRequestMessage>(body, RpcJson.Options)
                ?? throw new JsonException("La petición RPC está vacía.");

            _logger.LogInformation(
                "Petición RPC recibida. Operación: {Operation}; CorrelationId: {CorrelationId}; "
                    + "ReplyTo: {ReplyTo}; DeliveryTag: {DeliveryTag}.",
                request.Operation,
                eventArgs.BasicProperties.CorrelationId,
                eventArgs.BasicProperties.ReplyTo,
                eventArgs.DeliveryTag
            );

            // Un mismo formato podría admitir más operaciones en el futuro.
            if (!string.Equals(request.Operation, "weather", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"La operación '{request.Operation}' no está soportada."
                );
            }

            // Convierte el payload genérico al contrato esperado por esta operación.
            WeatherRequest weatherRequest =
                request.Payload.Deserialize<WeatherRequest>(RpcJson.Options)
                ?? throw new JsonException("Los datos de la petición no son válidos.");

            // Paso 5: ejecuta la lógica de negocio y crea una respuesta correcta.
            WeatherForecast[] forecasts = _weatherService.Create(weatherRequest.Days);
            response = RpcResponseMessage.Ok(forecasts);

            _logger.LogInformation(
                "Operación weather procesada. Días: {Days}; Pronósticos: {Count}.",
                weatherRequest.Days,
                forecasts.Length
            );
        }
        catch (Exception exception)
        {
            // Los fallos de validación o procesamiento también se devuelven al
            // Gateway como respuesta RPC para que este pueda generar un HTTP 502.
            _logger.LogWarning(exception, "La petición RPC no pudo procesarse.");
            response = RpcResponseMessage.Fail(exception.Message);
        }

        string? replyTo = eventArgs.BasicProperties.ReplyTo;
        if (!string.IsNullOrWhiteSpace(replyTo))
        {
            // Paso 6: ReplyTo contiene la cola temporal amq.gen-... del Gateway.
            byte[] responseBody = JsonSerializer.SerializeToUtf8Bytes(response, RpcJson.Options);
            BasicProperties responseProperties = new()
            {
                ContentType = "application/json",

                // Se conserva el identificador para que el cliente encuentre la
                // TaskCompletionSource correspondiente entre todas las pendientes.
                CorrelationId = eventArgs.BasicProperties.CorrelationId,

                // La cola de respuesta es temporal, por lo que persistir este
                // mensaje en disco no aportaría ninguna ventaja.
                Persistent = false,
            };

            // De nuevo se usa el default exchange: routingKey es el nombre exacto
            // de la cola privada indicada por ReplyTo.
            await _channel.BasicPublishAsync(
                exchange: string.Empty,
                routingKey: replyTo,
                mandatory: false,
                basicProperties: responseProperties,
                body: responseBody
            );

            _logger.LogInformation(
                "Respuesta RPC publicada. CorrelationId: {CorrelationId}; "
                    + "ReplyTo: {ReplyTo}; Correcta: {IsSuccess}.",
                eventArgs.BasicProperties.CorrelationId,
                replyTo,
                response.IsSuccess
            );
        }

        // El ACK se envía después de publicar la respuesta. RabbitMQ puede retirar
        // definitivamente esta petición de rpc.weather.
        await _channel.BasicAckAsync(eventArgs.DeliveryTag, multiple: false);

        _logger.LogInformation(
            "ACK enviado para la petición. DeliveryTag: {DeliveryTag}.",
            eventArgs.DeliveryTag
        );
    }

    // Detiene primero BackgroundService y después libera canal y conexión.
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);
        await DisposeBrokerResourcesAsync();
    }

    // El orden importa: el canal pertenece a la conexión y se cierra primero.
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
    }
}
