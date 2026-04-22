FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS base
WORKDIR /app
EXPOSE 8080

RUN apt-get update && apt-get install -y --no-install-recommends \
    curl \
    libfontconfig1 \
    libfreetype6 \
    fonts-dejavu-core \
    && rm -rf /var/lib/apt/lists/*

FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

COPY ["src/TravelReservations.Api/TravelReservations.Api.csproj", "TravelReservations.Api/"]
COPY ["src/TravelApi.Domain/TravelApi.Domain.csproj", "TravelApi.Domain/"]
COPY ["src/TravelApi.Application/TravelApi.Application.csproj", "TravelApi.Application/"]
COPY ["src/TravelApi.Infrastructure/TravelApi.Infrastructure.csproj", "TravelApi.Infrastructure/"]
COPY ["src/TravelApi/", "TravelApi/"]

RUN dotnet restore "TravelReservations.Api/TravelReservations.Api.csproj"

COPY ["src/TravelReservations.Api/", "TravelReservations.Api/"]
COPY ["src/TravelApi.Domain/", "TravelApi.Domain/"]
COPY ["src/TravelApi.Application/", "TravelApi.Application/"]
COPY ["src/TravelApi.Infrastructure/", "TravelApi.Infrastructure/"]

WORKDIR "/src/TravelReservations.Api"
RUN dotnet publish "TravelReservations.Api.csproj" -c Release -o /app/publish --no-restore

FROM base AS final
WORKDIR /app
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "TravelReservations.Api.dll"]
