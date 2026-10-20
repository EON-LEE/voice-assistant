FROM node:22-bookworm-slim AS web
WORKDIR /work
COPY src/VoiceAssistant.Web/package*.json ./
RUN npm ci
COPY src/VoiceAssistant.Web/ ./
RUN npm run build

FROM mcr.microsoft.com/dotnet/sdk:10.0-noble AS build
WORKDIR /work
ARG NUGET_SOURCE=https://api.nuget.org/v3/index.json
COPY src/VoiceAssistant.Api/VoiceAssistant.Api.csproj src/VoiceAssistant.Api/
RUN dotnet restore src/VoiceAssistant.Api/VoiceAssistant.Api.csproj --source "$NUGET_SOURCE"
COPY src/VoiceAssistant.Api/ src/VoiceAssistant.Api/
RUN dotnet publish src/VoiceAssistant.Api/VoiceAssistant.Api.csproj -c Release --no-restore -o /out /p:UseAppHost=false
COPY --from=web /work/dist /out/wwwroot

FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble AS runtime
# Ubuntu 24.04 ships ALSA as libasound2t64 (Debian's libasound2); the Speech SDK's audio extension links against it.
RUN apt-get update && apt-get install -y --no-install-recommends libasound2t64 ca-certificates \
    && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /out/ ./
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
USER $APP_UID
ENTRYPOINT ["dotnet", "VoiceAssistant.Api.dll"]
