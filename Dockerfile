# ==============================================================================
# Multi-stage Dockerfile para SIGETOUR (ASP.NET Core .NET 10.0)
# Optimizado para Render, Railway, AWS ECS y Docker local
# ==============================================================================

# STAGE 1: Build & Restore
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Copiar csproj y restaurar dependencias (aprovechando caché de capas Docker)
COPY ["SIGETOUR.API/SIGETOUR.API.csproj", "SIGETOUR.API/"]
RUN dotnet restore "SIGETOUR.API/SIGETOUR.API.csproj"

# Copiar todo el código fuente y publicar en modo Release
COPY . .
WORKDIR "/src/SIGETOUR.API"
RUN dotnet publish "SIGETOUR.API.csproj" -c Release -o /app/publish /p:UseAppHost=false

# STAGE 2: Runtime Image
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app

# Puerto por defecto (8080 para Docker local / overridden por PORT en Render)
ENV PORT=8080
EXPOSE 8080
EXPOSE 10000

# Variables de entorno base para ASP.NET Core en producción
ENV ASPNETCORE_ENVIRONMENT=Production
ENV DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=false

# Copiar artefactos compilados desde el stage anterior
COPY --from=build /app/publish .

# Crear directorios para subida de fotos (tours y vehículos) con permisos para el usuario app
RUN mkdir -p /app/wwwroot/images/tours /app/wwwroot/images/vehicles \
    && chown -R app:app /app \
    && chmod -R 775 /app/wwwroot/images

# Ejecutar el contenedor bajo el usuario seguro no privilegiado 'app' (Regla SonarQube docker:S6471)
USER app

# Punto de entrada de la aplicación
ENTRYPOINT ["dotnet", "SIGETOUR.API.dll"]
