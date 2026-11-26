# The gateway API as a container image. Built with the .NET SDK, then run on Microsoft's "chiseled" ASP.NET Core
# runtime image: no shell, no package manager, a non-root user. The "extra" variant adds the time zone database
# (days and deadlines are in India Standard Time) and culture data. Both versions are pinned; raise them (and
# rebuild) to pick up security fixes, for example monthly.
ARG SDK_IMAGE=mcr.microsoft.com/dotnet/sdk:10.0.404-noble
ARG RUNTIME_IMAGE=mcr.microsoft.com/dotnet/aspnet:10.0.15-noble-chiseled-extra

FROM ${SDK_IMAGE} AS build
WORKDIR /source

# Restore first, so the package layer is reused until a project file or package version changes.
COPY global.json Directory.Build.props Directory.Packages.props ./
COPY src/FiatCryptoGateway.Domain/FiatCryptoGateway.Domain.csproj src/FiatCryptoGateway.Domain/
COPY src/FiatCryptoGateway.Application/FiatCryptoGateway.Application.csproj src/FiatCryptoGateway.Application/
COPY src/FiatCryptoGateway.Sandbox/FiatCryptoGateway.Sandbox.csproj src/FiatCryptoGateway.Sandbox/
COPY src/FiatCryptoGateway.Persistence.Postgres/FiatCryptoGateway.Persistence.Postgres.csproj src/FiatCryptoGateway.Persistence.Postgres/
COPY src/FiatCryptoGateway.Api/FiatCryptoGateway.Api.csproj src/FiatCryptoGateway.Api/
RUN dotnet restore src/FiatCryptoGateway.Api/FiatCryptoGateway.Api.csproj

COPY src/ src/
COPY db/migrations/ db/migrations/
RUN dotnet publish src/FiatCryptoGateway.Api/FiatCryptoGateway.Api.csproj \
      --configuration Release --no-restore --output /app -p:UseAppHost=false

FROM ${RUNTIME_IMAGE}
WORKDIR /app
COPY --from=build /app .

# The image's non-root user (UID 1654). The API listens on 8080 over plain HTTP inside the container network; the
# HTTPS proxy in front of it terminates TLS. There is no curl or shell, so the health check asks the API itself.
USER $APP_UID
EXPOSE 8080
HEALTHCHECK --interval=15s --timeout=5s --start-period=30s --retries=3 \
  CMD ["dotnet", "FiatCryptoGateway.Api.dll", "--health-check"]
ENTRYPOINT ["dotnet", "FiatCryptoGateway.Api.dll"]
