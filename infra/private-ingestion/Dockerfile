# mcr.microsoft.com/powershell has only preview 7.6 tags; the .NET 10 SDK image ships PowerShell 7.6 LTS on .NET 10.
FROM mcr.microsoft.com/dotnet/sdk:10.0-noble
WORKDIR /work
COPY scripts/infra/Knowledge.psm1 scripts/infra/KnowledgeSync.psm1 scripts/infra/ManagedIngestion.psm1 scripts/infra/Private-Ingestion.ps1 ./scripts/infra/
COPY infra/knowledge/index.json ./infra/knowledge/index.json
COPY infra/private-ingestion/approved-synthetic.md ./infra/private-ingestion/approved-synthetic.md
ENV POWERSHELL_TELEMETRY_OPTOUT=1 DOTNET_CLI_TELEMETRY_OPTOUT=1 HOME=/tmp
USER 65532:65532
ENTRYPOINT ["pwsh", "-NoLogo", "-NoProfile", "-NonInteractive", "-File", "/work/scripts/infra/Private-Ingestion.ps1"]
