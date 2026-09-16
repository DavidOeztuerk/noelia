FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG PROJECT_PATH
ARG PROJECT_DLL
ARG NOELIA_SOURCE=https://api.nuget.org/v3/index.json
WORKDIR /src

COPY . ./

RUN if [ "$NOELIA_SOURCE" = "https://api.nuget.org/v3/index.json" ]; then \
      dotnet restore "$PROJECT_PATH" --configfile NuGet.Docker.Config; \
    else \
      dotnet restore "$PROJECT_PATH" --configfile NuGet.Docker.Local.Config; \
    fi
RUN dotnet publish "$PROJECT_PATH" --no-restore --configuration Release --output /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
ARG PROJECT_DLL
WORKDIR /app
RUN apt-get update \
    && apt-get install --yes --no-install-recommends curl \
    && rm -rf /var/lib/apt/lists/*
COPY --from=build /app/publish ./
ENV APP_DLL=${PROJECT_DLL}
EXPOSE 8080
ENTRYPOINT ["sh", "-c", "dotnet \"$APP_DLL\""]
