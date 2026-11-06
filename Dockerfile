FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# The shared build settings and package versions are part of restoring.
COPY ["global.json", "Directory.Build.props", "Directory.Packages.props", "./"]
COPY ["src/SanctionAndForgive.Api/SanctionAndForgive.Api.csproj", "src/SanctionAndForgive.Api/"]
COPY ["src/SanctionAndForgive.Cli/SanctionAndForgive.Cli.csproj", "src/SanctionAndForgive.Cli/"]
COPY ["src/SanctionAndForgive.Data/SanctionAndForgive.Data.csproj", "src/SanctionAndForgive.Data/"]
COPY ["src/SanctionAndForgive.Lists/SanctionAndForgive.Lists.csproj", "src/SanctionAndForgive.Lists/"]
COPY ["src/SanctionAndForgive.Matching/SanctionAndForgive.Matching.csproj", "src/SanctionAndForgive.Matching/"]
RUN dotnet restore "src/SanctionAndForgive.Api/SanctionAndForgive.Api.csproj" \
    && dotnet restore "src/SanctionAndForgive.Cli/SanctionAndForgive.Cli.csproj"

# Both apps go to one folder: the Container App runs the API, the import job overrides the command with the CLI.
# They share every library, at the versions Directory.Packages.props pins.
COPY . .
RUN dotnet publish "src/SanctionAndForgive.Api/SanctionAndForgive.Api.csproj" -c Release -o /app/publish --no-restore \
    && dotnet publish "src/SanctionAndForgive.Cli/SanctionAndForgive.Cli.csproj" -c Release -o /app/publish --no-restore

# Chiseled: only what .NET needs, no shell or package manager. "extra" keeps ICU (culture-aware text: case folding
# and normalisation of Cyrillic names) and the time zones. No shell, so anything prepared goes in the build stage.
FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled-extra AS final
WORKDIR /app
COPY --from=build /app/publish .
USER $APP_UID

# The base image already listens on 8080 (ASPNETCORE_HTTP_PORTS=8080); don't set ASPNETCORE_URLS as well.
EXPOSE 8080

# Version metadata baked into the image (the deploy workflow passes the commit), so the image describes itself
# wherever it runs. Read by the apps as App:Version etc., and exposed as standard OCI labels.
ARG VERSION=dev
ARG REVISION=
ARG COMMIT_DATE=
ARG SOURCE_URL=
LABEL org.opencontainers.image.version=$VERSION \
      org.opencontainers.image.revision=$REVISION \
      org.opencontainers.image.created=$COMMIT_DATE \
      org.opencontainers.image.source=$SOURCE_URL \
      org.opencontainers.image.licenses=MIT
ENV App__Version=$VERSION \
    App__Revision=$REVISION \
    App__CommitDate=$COMMIT_DATE \
    App__SourceUrl=$SOURCE_URL

ENTRYPOINT ["dotnet", "SanctionAndForgive.Api.dll"]
