# AgentForge.Verticals.Travel

Reference Travel vertical plugin for AgentForge, provided by QLoop Technologies.

This repository demonstrates how an external vertical owns its prompt, customer configuration, MCP tools/resources, domain data, media assets, and scheduled message content while the AgentForge core platform owns WhatsApp transport, queueing, AI orchestration, hosting, health checks, and outbound sending.

## Build and Test

```bash
dotnet restore AgentForge.Verticals.Travel.slnx --source https://api.nuget.org/v3/index.json --no-cache
dotnet test AgentForge.Verticals.Travel.slnx --no-restore
```

The solution consumes the QLoop-provided `AgentForge.Verticals.Abstractions` package from NuGet.org. This keeps the Travel vertical aligned with how customer-owned vertical plugins reference the public AgentForge contract package.

## Publish Plugin Bundle

```bash
dotnet publish src/AgentForge.Verticals.Travel/AgentForge.Verticals.Travel.csproj -c Release -o ../AgentForge.Core/artifacts/plugins/travel
```

Configure the core AgentForge platform with either:

```bash
VERTICAL_PLUGIN_PATH=/absolute/path/to/artifacts/plugins/travel
```

or:

```bash
VERTICAL_PLUGIN_ROOT=/absolute/path/to/artifacts/plugins
VERTICAL_ID=travel
```

Repository: https://github.com/qloop-tech/AgentForge.Verticals.Travel
