FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY global.json ./
COPY src/adgroups2cmdbuild/adgroups2cmdbuild.csproj src/adgroups2cmdbuild/
RUN dotnet restore src/adgroups2cmdbuild/adgroups2cmdbuild.csproj

COPY src/adgroups2cmdbuild/ src/adgroups2cmdbuild/
RUN dotnet publish src/adgroups2cmdbuild/adgroups2cmdbuild.csproj \
    --configuration Release \
    --output /app/publish \
    --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

RUN apt-get update \
    && apt-get install -y --no-install-recommends curl \
    && rm -rf /var/lib/apt/lists/*

ENV ASPNETCORE_URLS=http://0.0.0.0:8080
EXPOSE 8080

COPY --from=build /app/publish .
HEALTHCHECK --interval=30s --timeout=5s --retries=3 CMD curl -fsS http://localhost:8080/health || exit 1
ENTRYPOINT ["dotnet", "adgroups2cmdbuild.dll"]
