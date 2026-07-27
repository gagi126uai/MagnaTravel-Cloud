FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS base
WORKDIR /app
EXPOSE 8080

# QuestPDF Dependencies (Fonts & Libs)
RUN apt-get update && apt-get install -y --no-install-recommends \
    curl \
    ca-certificates \
    gnupg \
    libfontconfig1 \
    libfreetype6 \
    fonts-dejavu-core \
    && rm -rf /var/lib/apt/lists/*

# Obra "Empezar de cero" (2026-07-27): postgresql-client-16 (pg_dump) para el backup OBLIGATORIO que la API
# genera ella misma justo antes del borrado masivo (ver SystemDataWipeService/PgDumpAndMinioWipeBackupPort).
# El servidor es postgres:16 (docker-compose.yml) - se instala el cliente 16 a proposito (pg_dump mas viejo que
# el servidor no esta soportado oficialmente). La imagen base (mcr.microsoft.com/dotnet/aspnet:8.0) es Debian 12
# "bookworm", que NO trae postgresql-client-16 en sus repos default (Debian 12 default es la 15) - hace falta
# agregar el repo oficial de PGDG (PostgreSQL Global Development Group).
#
# OJO si el dia de mañana Microsoft cambia el SO base de esta imagen: el codename "bookworm" queda hardcodeado
# a proposito (no se detecta con lsb_release, que ademas no viene instalado en esta imagen minima). Si la base
# cambia de SO, este apt-get update va a fallar con 404 (el repo de PGDG para "bookworm" no existiria en la
# nueva base) - ES INTENCIONAL: preferimos que el build FALLE RUIDOSAMENTE a que instale silenciosamente un
# pg_dump de una version de Debian que no es la real.
RUN install -d /usr/share/postgresql-common/pgdg \
    && curl -fsSL https://www.postgresql.org/media/keys/ACCC4CF8.asc -o /usr/share/postgresql-common/pgdg/apt.postgresql.org.asc \
    && echo "deb [signed-by=/usr/share/postgresql-common/pgdg/apt.postgresql.org.asc] https://apt.postgresql.org/pub/repos/apt bookworm-pgdg main" > /etc/apt/sources.list.d/pgdg.list \
    && apt-get update \
    && apt-get install -y --no-install-recommends postgresql-client-16 \
    && rm -rf /var/lib/apt/lists/*

FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Copy projects
COPY ["src/TravelApi/TravelApi.csproj", "TravelApi/"]
COPY ["src/TravelApi.Domain/TravelApi.Domain.csproj", "TravelApi.Domain/"]
COPY ["src/TravelApi.Application/TravelApi.Application.csproj", "TravelApi.Application/"]
COPY ["src/TravelApi.Infrastructure/TravelApi.Infrastructure.csproj", "TravelApi.Infrastructure/"]

# Restore
RUN dotnet restore "TravelApi/TravelApi.csproj"

# Copy source
COPY ["src/TravelApi/", "TravelApi/"]
COPY ["src/TravelApi.Domain/", "TravelApi.Domain/"]
COPY ["src/TravelApi.Application/", "TravelApi.Application/"]
COPY ["src/TravelApi.Infrastructure/", "TravelApi.Infrastructure/"]

WORKDIR "/src/TravelApi"
RUN dotnet publish "TravelApi.csproj" -c Release -o /app/publish --no-restore

FROM base AS final
WORKDIR /app
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "TravelApi.dll"]
