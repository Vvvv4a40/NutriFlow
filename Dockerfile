FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build

WORKDIR /src

COPY NutriFlow.Domain/NutriFlow.Domain.csproj NutriFlow.Domain/
COPY NutriFlow.Infrastructure/NutriFlow.Infrastructure.csproj NutriFlow.Infrastructure/
COPY NutriFlow.Api/NutriFlow.Api.csproj NutriFlow.Api/
RUN dotnet restore NutriFlow.Api/NutriFlow.Api.csproj

COPY . .
RUN dotnet publish NutriFlow.Api/NutriFlow.Api.csproj \
    --configuration Release \
    --no-restore \
    --output /app/publish \
    /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime

WORKDIR /app

ENV ASPNETCORE_HTTP_PORTS=8080 \
    Database__Path=/data/nutriflow.db \
    Database__ApplyMigrationsOnStartup=true \
    Storage__LabelPhotosPath=/data/label-photos \
    Ai__Provider=Fake \
    Demo__SeedData=true

RUN mkdir -p /data/label-photos && chown -R $APP_UID /data

COPY --from=build --chown=$APP_UID /app/publish .

USER $APP_UID

EXPOSE 8080
VOLUME ["/data"]

ENTRYPOINT ["dotnet", "NutriFlow.Api.dll"]
