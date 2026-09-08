# syntax=docker/dockerfile:1
# Build context: repository root. The worker is the only cloud image that contains the compute engine
# (CabinetNC.Compute.Core + CabinetNC.Domain).

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG SOURCE_REVISION=
WORKDIR /src

COPY dotnet/src/CabinetNC.Domain/CabinetNC.Domain.csproj dotnet/src/CabinetNC.Domain/
COPY dotnet/src/CabinetNC.Domain.Compute/CabinetNC.Domain.Compute.csproj dotnet/src/CabinetNC.Domain.Compute/
COPY dotnet/src/CabinetNC.Compute.Core/CabinetNC.Compute.Core.csproj dotnet/src/CabinetNC.Compute.Core/
COPY dotnet/src/CabinetNC.Cloud.Contracts/CabinetNC.Cloud.Contracts.csproj dotnet/src/CabinetNC.Cloud.Contracts/
COPY dotnet/src/CabinetNC.Cloud.NestContract/CabinetNC.Cloud.NestContract.csproj dotnet/src/CabinetNC.Cloud.NestContract/
COPY dotnet/src/CabinetNC.Cloud.Infrastructure/CabinetNC.Cloud.Infrastructure.csproj dotnet/src/CabinetNC.Cloud.Infrastructure/
COPY dotnet/src/CabinetNC.Cloud.Worker/CabinetNC.Cloud.Worker.csproj dotnet/src/CabinetNC.Cloud.Worker/
RUN dotnet restore dotnet/src/CabinetNC.Cloud.Worker/CabinetNC.Cloud.Worker.csproj

COPY dotnet/src/CabinetNC.Domain/ dotnet/src/CabinetNC.Domain/
COPY dotnet/src/CabinetNC.Domain.Compute/ dotnet/src/CabinetNC.Domain.Compute/
COPY dotnet/src/CabinetNC.Compute.Core/ dotnet/src/CabinetNC.Compute.Core/
COPY dotnet/src/CabinetNC.Cloud.Contracts/ dotnet/src/CabinetNC.Cloud.Contracts/
COPY dotnet/src/CabinetNC.Cloud.NestContract/ dotnet/src/CabinetNC.Cloud.NestContract/
COPY dotnet/src/CabinetNC.Cloud.Infrastructure/ dotnet/src/CabinetNC.Cloud.Infrastructure/
COPY dotnet/src/CabinetNC.Cloud.Worker/ dotnet/src/CabinetNC.Cloud.Worker/
RUN dotnet publish dotnet/src/CabinetNC.Cloud.Worker/CabinetNC.Cloud.Worker.csproj \
      -c Release --no-restore -o /app \
      -p:SourceRevisionId=${SOURCE_REVISION} -p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/runtime:10.0 AS runtime
# Npgsql probes for Kerberos (GSSAPI) support at startup and logs an error when the library is absent.
RUN apt-get update \
 && apt-get install -y --no-install-recommends libgssapi-krb5-2 \
 && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /app ./
ENV DOTNET_gcServer=0
USER $APP_UID
ENTRYPOINT ["dotnet", "CabinetNC.Cloud.Worker.dll"]
