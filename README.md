# RPC con RabbitMQ y .NET 10

Ejemplo educativo de comunicación productor-consumidor mediante el patrón **RPC sobre RabbitMQ**. Un Gateway recibe una petición HTTP, publica una solicitud en RabbitMQ y espera la respuesta generada por un segundo servicio.

Para estudiar primero el mecanismo esencial con solo una API y una consola, consulta [`RabbitMQBasicExample`](https://github.com/sergioocode/RabbitMQBasicExample).

Docker Compose se utiliza únicamente para ejecutar el servidor RabbitMQ. Las aplicaciones .NET se inician directamente desde Visual Studio en Windows.

La rama [`dockerfile-vs`](https://github.com/sergioocode/RabbitMQAdvancedExample/tree/dockerfile-vs) contiene la variante que ejecuta RabbitMQ, Gateway y Weather como contenedores administrados conjuntamente desde Visual Studio.

La solución utiliza directamente `RabbitMQ.Client` 7.2.1 e implementa explícitamente la conexión, los canales, las colas, `CorrelationId`, `ReplyTo`, los timeouts y los acknowledgements.

![Flujo RPC entre Gateway, RabbitMQ y Weather](docs/rabbitmq-rpc-flow.png)

## Arquitectura

| Proyecto | Responsabilidad |
| --- | --- |
| `RabbitMqExample.Gateway.Api` | Recibe HTTP, valida la entrada, realiza la llamada RPC y convierte errores remotos en respuestas HTTP. |
| `RabbitMqExample.Weather.Api` | Mantiene un consumidor en segundo plano, procesa `rpc.weather` y publica la respuesta. |
| `RabbitMqExample.Messaging` | Implementa el cliente RPC, los sobres de mensajes, la configuración y la correlación. |
| `RabbitMqExample.Contracts` | Define `WeatherRequest` y `WeatherForecast`, compartidos por los dos servicios. |
| `RabbitMqExample.Tests` | Prueba la lógica meteorológica sin necesitar RabbitMQ. |

Aunque el consumidor se llama `Weather.Api`, los pronósticos no se solicitan mediante un endpoint HTTP suyo. El trabajo llega por RabbitMQ; sus endpoints HTTP solo informan sobre el servicio y su estado.

## Recorrido de una petición

1. El cliente llama a `GET /weather?days=5` en el Gateway.
2. El Gateway valida que `days` esté entre 1 y 14.
3. `RabbitMqRpcClient` crea un `CorrelationId` único y registra la llamada como pendiente.
4. La solicitud se serializa como JSON y se publica en la cola duradera `rpc.weather`.
5. El mensaje incluye `ReplyTo`, con el nombre de una cola temporal `amq.gen-...` creada por el Gateway.
6. `WeatherRpcWorker` consume la solicitud, deserializa `WeatherRequest` y ejecuta `WeatherForecastService`.
7. Weather publica la respuesta en la cola indicada por `ReplyTo`, conservando el mismo `CorrelationId`.
8. Weather confirma la petición original mediante un ACK manual.
9. El cliente RPC recibe la respuesta, localiza la llamada pendiente por `CorrelationId` y completa su `await`.
10. El Gateway devuelve el resultado como HTTP 200 y JSON.

El ejemplo usa el **default exchange** de RabbitMQ (`exchange: ""`). En este exchange, el `routingKey` coincide directamente con el nombre de la cola de destino.

## Requisitos

- .NET SDK 10.
- Visual Studio 2026.
- La carga de trabajo de Visual Studio para desarrollo de ASP.NET.
- Docker Desktop con Docker Compose.
- Puertos libres `5672` y `15672` para RabbitMQ.

## Ejecución

### 1. Iniciar RabbitMQ

Desde la raíz de la solución:

```powershell
docker compose up -d
```

Este comando descarga la imagen si no existe, crea el contenedor y levanta RabbitMQ. El panel de administración queda disponible en `http://localhost:15672`.

```text
Usuario:    app
Contraseña: app
```

### 2. Iniciar las aplicaciones desde Visual Studio

Configura varios proyectos de inicio:

| Proyecto | Acción |
| --- | --- |
| `RabbitMqExample.Weather.Api` | Inicio |
| `RabbitMqExample.Gateway.Api` | Inicio |

Weather debe quedar escuchando la cola `rpc.weather`; el Gateway crea su cola temporal de respuestas y atiende las peticiones HTTP.

Selecciona el perfil `http` o `https` de cada API. Ambas se ejecutan directamente en Windows y se conectan a RabbitMQ mediante `localhost:5672`.

### 3. Probar el flujo RPC

Utiliza la petición incluida en `RabbitMqExample.Gateway.Api.http`:

```http
GET http://localhost:5058/weather?days=5
```

Endpoints disponibles durante la ejecución desde Visual Studio:

| Dirección | Uso |
| --- | --- |
| `http://localhost:5058/weather?days=5` | Petición HTTP que activa el RPC completo. |
| `http://localhost:5058/health` | Estado del cliente RPC del Gateway. |
| `http://localhost:5210/` | Información del consumidor Weather. |
| `http://localhost:5210/health` | Estado de la conexión y canal del consumidor. |
| `http://localhost:15672` | Panel RabbitMQ Management. |

### 4. Detener RabbitMQ

```powershell
docker compose down
```

El volumen se conserva. Para eliminar también los datos del broker se puede utilizar `docker compose down -v`.

## Objetos creados en RabbitMQ

### `rpc.weather`

- Cola de solicitudes.
- Nombre conocido y configurado en ambos servicios.
- Duradera, no exclusiva y sin borrado automático.
- Las solicitudes se publican como persistentes.

### `amq.gen-...`

- Cola de respuestas creada por RabbitMQ al iniciar el Gateway.
- Su nombre cambia en cada nueva conexión.
- Exclusiva y con borrado automático.
- Nunca debe escribirse su nombre directamente en el código.

### `CorrelationId` y `ReplyTo`

- `ReplyTo` indica a Weather dónde debe publicar la respuesta.
- `CorrelationId` permite relacionarla con la petición correcta.
- `ConcurrentDictionary` conserva las llamadas que están esperando respuesta.

### ACK y `prefetchCount`

Weather consume con `autoAck: false` y confirma cada solicitud después de publicar su respuesta. `prefetchCount: 1` evita que el consumidor acumule varias peticiones sin confirmar.

## Configuración

La sección `RabbitMq` está presente en los dos archivos `appsettings.json`:

| Opción | Significado | Valor local |
| --- | --- | --- |
| `HostName` | Dirección del broker. | `localhost` |
| `Port` | Puerto AMQP. | `5672` |
| `UserName` | Usuario RabbitMQ. | `app` |
| `Password` | Contraseña RabbitMQ. | `app` |
| `VirtualHost` | Espacio lógico utilizado. | `/` |
| `RequestQueue` | Cola de solicitudes RPC. | `rpc.weather` |
| `RequestTimeoutSeconds` | Espera máxima del Gateway. | `10` |

## Respuestas HTTP del Gateway

| Código | Situación |
| --- | --- |
| `200` | Weather respondió correctamente. |
| `400` | `days` está fuera del rango 1 a 14. |
| `502` | Weather procesó la solicitud, pero devolvió un error remoto. |
| `503` | El cliente RPC no está conectado a RabbitMQ. |
| `504` | No llegó una respuesta antes del timeout. |

## Estructura

```text
RabbitMQ-Ejemplo-Avanzado/
|-- compose.yaml
|-- RabbitMqExample.sln
|-- docs/
|   `-- rabbitmq-rpc-flow.png
|-- src/
|   |-- RabbitMqExample.Contracts/
|   |-- RabbitMqExample.Messaging/
|   |-- RabbitMqExample.Gateway.Api/
|   `-- RabbitMqExample.Weather.Api/
`-- tests/
    `-- RabbitMqExample.Tests/
```

## Solución de problemas

### RabbitMQ continúa en `starting` o aparece como `unhealthy`

```powershell
docker compose logs rabbitmq
```

El healthcheck permite hasta 30 segundos por diagnóstico y un minuto inicial para que RabbitMQ termine de arrancar.

### El Gateway devuelve 503 o 504

Comprueba los endpoints `/health` del Gateway y Weather y revisa sus consolas en Visual Studio.

### Los puertos de RabbitMQ ya están ocupados

Detén el otro proyecto que esté utilizando `5672` o `15672` antes de iniciar este Compose.

## Alcance educativo

El proyecto muestra claramente el mecanismo RPC, pero RPC sobre mensajería mantiene acoplado el tiempo de respuesta del Gateway al consumidor. Para trabajos que no necesitan respuesta inmediata suele ser preferible publicar eventos asíncronos, confirmar rápidamente al cliente y procesarlos fuera de la petición HTTP.
