FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS base
WORKDIR /app
EXPOSE 8080

# QuestPDF Dependencies (Fonts & Libs)
RUN apt-get update && apt-get install -y --no-install-recommends \
    libfontconfig1 \
    libfreetype6 \
    fonts-dejavu-core \
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
