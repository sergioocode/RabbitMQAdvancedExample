using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using RabbitMqExample.Messaging;
using RabbitMqExample.Weather.Api;

// Weather usa el host de ASP.NET para disponer de configuración, inyección de
// dependencias y /health, pero el trabajo real llega por RabbitMQ, no por HTTP.
WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

// Lee y valida la misma sección RabbitMq que utiliza el Gateway.
builder
    .Services.AddOptions<RabbitMqOptions>()
    .Bind(builder.Configuration.GetSection(RabbitMqOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();

// TimeProvider se inyecta para que la fecha pueda sustituirse en los tests.
builder.Services.AddSingleton(TimeProvider.System);

// Servicio de dominio que genera los datos sin conocer RabbitMQ.
builder.Services.AddSingleton<IWeatherForecastService, WeatherForecastService>();

// La misma instancia de WeatherRpcWorker se registra como singleton, servicio en
// segundo plano y fuente de estado para WeatherRpcHealthCheck.
builder.Services.AddSingleton<WeatherRpcWorker>();
builder.Services.AddHostedService(services => services.GetRequiredService<WeatherRpcWorker>());
builder.Services.AddHealthChecks().AddCheck<WeatherRpcHealthCheck>("rabbitmq");

WebApplication app = builder.Build();

// Endpoint informativo; no genera pronósticos ni sustituye a la cola RPC.
app.MapGet(
    "/",
    () =>
        Results.Ok(
            new
            {
                service = "RabbitMqExample.Weather.Api",
                consumes = "rpc.weather",
                operation = "weather",
            }
        )
);

// Indica si el BackgroundService mantiene abierta su conexión y su canal.
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
