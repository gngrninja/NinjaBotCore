# Build stage
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

# Copy project file and restore
COPY NinjaBotHelpers.csproj ./
RUN dotnet restore

# Copy source and build
COPY . .
RUN dotnet publish -c Release -o /app/publish /p:UseAppHost=false

# Runtime stage
FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS final
WORKDIR /app

# Create non-root user for security
RUN groupadd -r ninjabot && useradd -r -g ninjabot ninjabot

# Copy published files
COPY --from=build /app/publish .

# Create logs directory
RUN mkdir -p /app/logs && chown -R ninjabot:ninjabot /app

# Switch to non-root user
USER ninjabot

# Health check (simple file existence check, as this is a worker service)
HEALTHCHECK --interval=30s --timeout=3s --start-period=10s --retries=3 \
  CMD test -f /app/NinjaBotHelpers.dll || exit 1

ENTRYPOINT ["dotnet", "NinjaBotHelpers.dll"]
