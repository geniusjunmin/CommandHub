FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY CommandHub.sln Directory.Build.props global.json .editorconfig ./
COPY src/CommandHub.Domain/CommandHub.Domain.csproj src/CommandHub.Domain/
COPY src/CommandHub.Application/CommandHub.Application.csproj src/CommandHub.Application/
COPY src/CommandHub.Infrastructure/CommandHub.Infrastructure.csproj src/CommandHub.Infrastructure/
COPY src/CommandHub.Web/CommandHub.Web.csproj src/CommandHub.Web/
RUN dotnet restore src/CommandHub.Web/CommandHub.Web.csproj
COPY src/ src/
RUN dotnet publish src/CommandHub.Web/CommandHub.Web.csproj --configuration Release --no-restore --output /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
USER root
RUN apt-get update \
    && apt-get install --yes --no-install-recommends curl \
    && rm -rf /var/lib/apt/lists/* \
    && mkdir -p /app/keys \
    && chown -R app:app /app
WORKDIR /app
COPY --from=build --chown=app:app /app/publish .
USER app
EXPOSE 8080
HEALTHCHECK --interval=30s --timeout=5s --start-period=20s --retries=3 CMD curl --fail --silent http://localhost:8080/health/live || exit 1
ENTRYPOINT ["dotnet", "CommandHub.Web.dll"]
