ARG DOTNET_VERSION=11.0

FROM mcr.microsoft.com/dotnet/sdk:${DOTNET_VERSION} AS build
WORKDIR /src

COPY dotnet/ ./dotnet/
COPY sqlite/schema.sql ./sqlite/schema.sql
COPY sqlite/migrations/002_processing.sql ./sqlite/migrations/002_processing.sql
RUN dotnet restore dotnet/FireData.Api/FireData.Api.csproj
RUN dotnet publish dotnet/FireData.Api/FireData.Api.csproj \
    --configuration Release \
    --no-restore \
    --output /app/publish \
    /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:${DOTNET_VERSION} AS runtime
WORKDIR /app
COPY --from=build /app/publish/ ./

ENV ASPNETCORE_HTTP_PORTS=8080 \
    ASPNETCORE_ENVIRONMENT=Production \
    ConnectionStrings__FireData="Data Source=/data/fire-observations.sqlite;Mode=ReadOnly;Foreign Keys=True"

EXPOSE 8080
USER $APP_UID
ENTRYPOINT ["dotnet", "FireData.Api.dll"]
