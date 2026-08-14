# Etapa base utilizada por Visual Studio durante la depuración rápida.
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS base
USER app
WORKDIR /app
EXPOSE 8080

# Etapa de compilación con el SDK de .NET.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src

# Copia primero los proyectos para aprovechar la caché de restauración.
COPY ["src/RabbitMqExample.Weather.Api/RabbitMqExample.Weather.Api.csproj", "src/RabbitMqExample.Weather.Api/"]
COPY ["src/RabbitMqExample.Contracts/RabbitMqExample.Contracts.csproj", "src/RabbitMqExample.Contracts/"]
COPY ["src/RabbitMqExample.Messaging/RabbitMqExample.Messaging.csproj", "src/RabbitMqExample.Messaging/"]
RUN dotnet restore "src/RabbitMqExample.Weather.Api/RabbitMqExample.Weather.Api.csproj"

COPY . .
WORKDIR "/src/src/RabbitMqExample.Weather.Api"
RUN dotnet build "RabbitMqExample.Weather.Api.csproj" -c "$BUILD_CONFIGURATION" -o /app/build

# Publica la aplicación sin generar un ejecutable nativo para el host.
FROM build AS publish
ARG BUILD_CONFIGURATION=Release
RUN dotnet publish "RabbitMqExample.Weather.Api.csproj" -c "$BUILD_CONFIGURATION" -o /app/publish /p:UseAppHost=false

# Imagen final utilizada fuera del modo rápido de Visual Studio.
FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "RabbitMqExample.Weather.Api.dll"]
