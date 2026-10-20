FROM mcr.microsoft.com/dotnet/sdk:10.0-noble AS build
WORKDIR /work
ARG NUGET_SOURCE=https://api.nuget.org/v3/index.json
COPY src/VoiceAssistant.Api/VoiceAssistant.Api.csproj src/VoiceAssistant.Api/
COPY tools/VoiceAssistant.LiveProbe/VoiceAssistant.LiveProbe.csproj tools/VoiceAssistant.LiveProbe/
RUN dotnet restore tools/VoiceAssistant.LiveProbe/VoiceAssistant.LiveProbe.csproj --source "$NUGET_SOURCE"
COPY src/VoiceAssistant.Api/ src/VoiceAssistant.Api/
COPY tools/VoiceAssistant.LiveProbe/ tools/VoiceAssistant.LiveProbe/
RUN dotnet publish tools/VoiceAssistant.LiveProbe/VoiceAssistant.LiveProbe.csproj \
    -c Release --no-restore -o /out /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble AS runtime
# Ubuntu 24.04 ships ALSA as libasound2t64 (Debian's libasound2); the Speech SDK's audio extension links against it.
RUN apt-get update && apt-get install -y --no-install-recommends libasound2t64 ca-certificates \
    && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /out/ ./
COPY tests/VoiceAssistant.Web.E2E/fixtures/original-project.wav fixtures/original-project.wav
COPY tests/VoiceAssistant.Web.E2E/fixtures/original-project.json fixtures/original-project.json
ENV Provider__Mode=Azure DOTNET_ENVIRONMENT=Production
USER $APP_UID
ENTRYPOINT ["dotnet", "VoiceAssistant.LiveProbe.dll", "--live", "--audio", "/app/fixtures/original-project.wav", "--metadata", "/app/fixtures/original-project.json", "--timeout-seconds", "120"]
