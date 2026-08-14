using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using RabbitMqExample.Contracts;
using RabbitMqExample.Gateway.Api;
using RabbitMqExample.Messaging;

// Gateway es la puerta de entrada HTTP. No calcula el pronóstico: convierte la
// petición HTTP en una llamada RPC a través de RabbitMQ y devuelve su respuesta.
WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

// Enlaza appsettings.json con RabbitMqOptions. ValidateOnStart hace que una
// configuración inválida detenga el arranque en vez de fallar en la primera llamada.
builder
    .Services.AddOptions<RabbitMqOptions>()
    .Bind(builder.Configuration.GetSection(RabbitMqOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();

// Las tres inscripciones apuntan a la MISMA instancia:
// - RabbitMqRpcClient permite consultar su estado concreto.
// - IRabbitMqRpcClient desacopla el endpoint de la implementación.
// - IHostedService abre y cierra la conexión junto con la aplicación.
builder.Services.AddSingleton<RabbitMqRpcClient>();
builder.Services.AddSingleton<IRabbitMqRpcClient>(services =>
    services.GetRequiredService<RabbitMqRpcClient>()
);
builder.Services.AddHostedService(services => services.GetRequiredService<RabbitMqRpcClient>());

// /health comprobará la conexión real del cliente, no solo que la API responde.
builder.Services.AddHealthChecks().AddCheck<RpcClientHealthCheck>("rabbitmq");

WebApplication app = builder.Build();

// Categoría específica para que las entradas del flujo HTTP se distingan de
// los mensajes internos de ASP.NET y del cliente RabbitMQ.
ILogger requestLogger = app
    .Services.GetRequiredService<ILoggerFactory>()
    .CreateLogger("RabbitMqExample.Gateway.Api.Requests");

// Endpoint informativo para descubrir el servicio y su ruta principal.
app.MapGet(
    "/",
    () => Results.Ok(new { service = "RabbitMqExample.Gateway.Api", endpoint = "/weather?days=5" })
);

// Paso 1: el cliente inicia el flujo con GET /weather?days=5.
app.MapGet(
    "/weather",
    async Task<IResult> (
        int? days,
        IRabbitMqRpcClient rpcClient,
        CancellationToken cancellationToken
    ) =>
    {
        // Si days no llega en la URL, se solicitan cinco días por defecto.
        int requestedDays = days ?? 5;

        requestLogger.LogInformation(
            "HTTP GET /weather recibido. Días solicitados: {Days}.",
            requestedDays
        );

        // Se valida antes de enviar mensajes para evitar trabajo remoto innecesario.
        if (requestedDays is < 1 or > 14)
        {
            requestLogger.LogWarning(
                "Petición HTTP rechazada: {Days} está fuera del rango 1..14.",
                requestedDays
            );

            return Results.ValidationProblem(
                new Dictionary<string, string[]>
                {
                    ["days"] = ["El valor debe estar entre 1 y 14."],
                }
            );
        }

        try
        {
            // Pasos 2-7: RabbitMqRpcClient publica WeatherRequest en rpc.weather,
            // espera la respuesta correlacionada y devuelve WeatherForecast[].
            WeatherForecast[] forecasts = await rpcClient.CallAsync<
                WeatherRequest,
                WeatherForecast[]
            >(operation: "weather", request: new WeatherRequest(requestedDays), cancellationToken);

            requestLogger.LogInformation(
                "Respuesta RPC completada. Se devolverán {Count} pronósticos con HTTP 200.",
                forecasts.Length
            );

            return Results.Ok(forecasts);
        }
        catch (TimeoutException exception)
        {
            // 504: el Gateway estaba disponible, pero el servicio remoto no respondió.
            requestLogger.LogWarning(exception, "La llamada RPC superó el tiempo de espera.");

            return Results.Problem(
                title: "El servicio meteorológico no respondió a tiempo.",
                detail: exception.Message,
                statusCode: StatusCodes.Status504GatewayTimeout
            );
        }
        catch (InvalidOperationException exception)
        {
            // 503: el cliente RPC todavía no está conectado al broker.
            requestLogger.LogWarning(
                exception,
                "RabbitMQ no está disponible para la petición HTTP."
            );

            return Results.Problem(
                title: "RabbitMQ no está disponible.",
                detail: exception.Message,
                statusCode: StatusCodes.Status503ServiceUnavailable
            );
        }
        catch (RemoteRpcException exception)
        {
            // 502: sí hubo respuesta remota, pero Weather devolvió un error.
            requestLogger.LogWarning(exception, "Weather devolvió un error RPC.");

            return Results.Problem(
                title: "El servicio meteorológico rechazó la petición.",
                detail: exception.Message,
                statusCode: StatusCodes.Status502BadGateway
            );
        }
    }
);

// Expone el estado de la dependencia RabbitMQ en un JSON sencillo para personas,
// Docker, Kubernetes u otro sistema de monitorización.
app.MapHealthChecks(
    "/health",
    new HealthCheckOptions
    {
        ResponseWriter = async (context, report) =>
        {
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsJsonAsync(
                new
                {
                    // Estado global y detalle individual de cada comprobación registrada.
                    status = report.Status.ToString(),
                    checks = report.Entries.ToDictionary(
                        entry => entry.Key,
                        entry => entry.Value.Status.ToString()
                    ),
                }
            );
        },
    }
);

app.Run();
