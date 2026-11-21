# The API image: the REST API, SqlOS, the desktop relay, the model gateway and the background jobs in one
# process. The web app is built separately by client/Dockerfile.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY api/Workstream.Api.csproj api/
RUN dotnet restore api/Workstream.Api.csproj
COPY api/ api/
# The app catalog (desktop/apps.json) is embedded in the API: one list for the API, the image and the panel.
COPY desktop/apps.json desktop/apps.json
# So are the emails' words (client/locales/emails), every word a key like the app's.
COPY client/locales/emails/ client/locales/emails/
RUN dotnet publish api/Workstream.Api.csproj -c Release -o /out --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /out/ ./
USER $APP_UID
EXPOSE 8080
ENTRYPOINT ["dotnet", "Workstream.dll"]
