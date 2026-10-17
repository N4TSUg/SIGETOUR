# Multi-stage Dockerfile for SIGETOUR (ASP.NET Core .NET 10.0)
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Copy project file and restore dependencies
COPY ["SIGETOUR.API/SIGETOUR.API.csproj", "SIGETOUR.API/"]
RUN dotnet restore "SIGETOUR.API/SIGETOUR.API.csproj"

# Copy full source and build
COPY . .
WORKDIR "/src/SIGETOUR.API"
RUN dotnet build "SIGETOUR.API.csproj" -c Release -o /app/build

# Publish release artifact
FROM build AS publish
RUN dotnet publish "SIGETOUR.API.csproj" -c Release -o /app/publish /p:UseAppHost=false

# Runtime image
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
EXPOSE 8080
EXPOSE 8081
ENV ASPNETCORE_URLS=http://+:8080
COPY --from=publish /app/publish .

ENTRYPOINT ["dotnet", "SIGETOUR.API.dll"]
