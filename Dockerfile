FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

# Copy solution & project files to leverage Docker layer caching
COPY NinjaBotCore.sln ./
COPY src/NinjaBotCore.csproj ./src/
RUN dotnet restore ./src/NinjaBotCore.csproj

# Copy the remaining source and publish the app
COPY . .
RUN dotnet publish ./src/NinjaBotCore.csproj -c Release -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS final
WORKDIR /app
COPY --from=build /app/publish .

# Expose log path for bind mounts
VOLUME ["/app/logs"]

ENTRYPOINT ["dotnet", "NinjaBotCore.dll"]
