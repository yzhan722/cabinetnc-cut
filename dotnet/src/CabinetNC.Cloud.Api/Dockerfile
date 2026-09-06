# syntax=docker/dockerfile:1
# Build context: repository root. Publishes the intranet API without the compute engine.

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG SOURCE_REVISION=
WORKDIR /src

# Restore first so dependency layers are reused while application code changes.
COPY dotnet/src/CabinetNC.Cloud.Contracts/CabinetNC.Cloud.Contracts.csproj dotnet/src/CabinetNC.Cloud.Contracts/
COPY dotnet/src/CabinetNC.Cloud.Infrastructure/CabinetNC.Cloud.Infrastructure.csproj dotnet/src/CabinetNC.Cloud.Infrastructure/
COPY dotnet/src/CabinetNC.Cloud.Api/CabinetNC.Cloud.Api.csproj dotnet/src/CabinetNC.Cloud.Api/
RUN dotnet restore dotnet/src/CabinetNC.Cloud.Api/CabinetNC.Cloud.Api.csproj

COPY dotnet/src/CabinetNC.Cloud.Contracts/ dotnet/src/CabinetNC.Cloud.Contracts/
COPY dotnet/src/CabinetNC.Cloud.Infrastructure/ dotnet/src/CabinetNC.Cloud.Infrastructure/
COPY dotnet/src/CabinetNC.Cloud.Api/ dotnet/src/CabinetNC.Cloud.Api/
RUN dotnet publish dotnet/src/CabinetNC.Cloud.Api/CabinetNC.Cloud.Api.csproj \
      -c Release --no-restore -o /app \
      -p:SourceRevisionId=${SOURCE_REVISION} -p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
# Npgsql probes for Kerberos (GSSAPI) support at startup and logs an error when the library is absent.
RUN apt-get update \
 && apt-get install -y --no-install-recommends libgssapi-krb5-2 \
 && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /app ./
ENV ASPNETCORE_HTTP_PORTS=8080 \
    DOTNET_gcServer=0
EXPOSE 8080
# The image ships a non-root "app" user; never run the API as root.
USER $APP_UID
ENTRYPOINT ["dotnet", "CabinetNC.Cloud.Api.dll"]
