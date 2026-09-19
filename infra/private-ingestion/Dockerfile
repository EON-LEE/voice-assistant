FROM mcr.microsoft.com/powershell:7.4-debian-12
WORKDIR /work
COPY scripts/infra/Knowledge.psm1 scripts/infra/KnowledgeSync.psm1 scripts/infra/ManagedIngestion.psm1 scripts/infra/Private-Ingestion.ps1 ./scripts/infra/
COPY infra/knowledge/index.json ./infra/knowledge/index.json
COPY infra/private-ingestion/approved-synthetic.md ./infra/private-ingestion/approved-synthetic.md
ENV POWERSHELL_TELEMETRY_OPTOUT=1 DOTNET_CLI_TELEMETRY_OPTOUT=1 HOME=/tmp
USER 65532:65532
ENTRYPOINT ["pwsh", "-NoLogo", "-NoProfile", "-NonInteractive", "-File", "/work/scripts/infra/Private-Ingestion.ps1"]
