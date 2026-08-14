# RPC con RabbitMQ, Dockerfile y Visual Studio

Variante del ejemplo avanzado en la que Visual Studio construye y ejecuta RabbitMQ, Gateway y Weather como una única aplicación Docker Compose.

Para estudiar primero el mecanismo esencial con solo una API y una consola, consulta [`RabbitMQBasicExample`](https://github.com/sergioocode/RabbitMQBasicExample).

## Arquitectura

| Proyecto | Responsabilidad |
| --- | --- |
| `RabbitMqExample.Gateway.Api` | Recibe HTTP, publica la solicitud RPC y espera la respuesta correlacionada. |
| `RabbitMqExample.Weather.Api` | Consume `rpc.weather`, genera los pronósticos y publica la respuesta. |
| `RabbitMqExample.Messaging` | Implementa conexión, canales, mensajes, correlación y timeouts. |
| `RabbitMqExample.Contracts` | Contiene los contratos compartidos. |
| `RabbitMqExample.Tests` | Prueba la lógica meteorológica sin necesitar RabbitMQ. |

![Flujo RPC entre Gateway, RabbitMQ y Weather](docs/rabbitmq-rpc-flow.png)

## Flujo RPC

1. El cliente llama a `GET /weather?days=5` en Gateway.
2. Gateway publica la solicitud persistente en `rpc.weather` con `ReplyTo` y `CorrelationId`.
3. Weather consume la solicitud y genera los pronósticos.
4. Weather publica la respuesta en la cola temporal indicada por `ReplyTo`.
5. Gateway relaciona la respuesta mediante `CorrelationId` y devuelve HTTP 200.

## Requisitos

- .NET SDK 10.
- Docker Desktop.
- Visual Studio con las herramientas de desarrollo de contenedores.

## Ejecutar desde Visual Studio

Establece `docker-compose` como único proyecto de inicio y ejecuta la solución. Visual Studio construye las dos imágenes .NET e inicia conjuntamente los tres servicios.

| Dirección | Uso |
| --- | --- |
| `http://localhost:8080/weather?days=5` | Gateway y flujo RPC completo. |
| `http://localhost:8080/health` | Estado del cliente RPC de Gateway. |
| `http://localhost:8081/` | Información del consumidor Weather. |
| `http://localhost:8081/health` | Estado de Weather. |
| `localhost:5673` | Protocolo AMQP. |
| `http://localhost:15673` | RabbitMQ Management (`app` / `app`). |

La petición de prueba está incluida en `src/RabbitMqExample.Gateway.Api/RabbitMqExample.Gateway.Api.http`.

## Archivos de contenedores

| Archivo | Función |
| --- | --- |
| `docker-compose.dcproj` | Integra Docker Compose como proyecto de Visual Studio. |
| `docker-compose.yml` | Define RabbitMQ, Gateway, Weather, red y volumen. |
| `Properties/launchSettings.json` | Indica qué servicios se depuran desde Visual Studio. |
| `src/RabbitMqExample.Gateway.Api/Dockerfile` | Construye la imagen de Gateway. |
| `src/RabbitMqExample.Weather.Api/Dockerfile` | Construye la imagen de Weather. |

La rama [`main`](https://github.com/sergioocode/RabbitMQAdvancedExample/tree/main) contiene la variante que ejecuta únicamente RabbitMQ en Docker y las aplicaciones .NET directamente en Windows.
