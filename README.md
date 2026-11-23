# Project Nexus

Internal communication for about 1,000 accounts and 200 active users. Chat supports direct messages and groups. The Communities tab organizes discussions by **Community → Channel**.

## Getting started

- [Agent instructions](AGENTS.md) and [documentation index](docs/README.md).
- [Implementation roadmap](docs/implementation-plan/ROADMAP.md), [M0.1 handoff](docs/implementation-plan/m0-foundation/HANDOFF-m0.1.md), and [decisions index](docs/decisions/INDEX.md).
- Open the [end-user prototype](docs/mockups/project-nexus-enduser-mockup.html) or [M1–M2 prototype](docs/mockups/project-nexus-m1-m2-mockup.html) directly in a browser.

## Repository layout

| Path | Responsibility |
|---|---|
| `src/Nexus.Web/` | ASP.NET Core host for the API, SignalR, and published React client. Feature modules live under `Modules/`. |
| `src/Nexus.Client/` | React, Vite, and TypeScript client. |
| `tests/Nexus.IntegrationTests/` | xUnit integration tests. M0.2 adds PostgreSQL with Testcontainers. |
| `tests/e2e/` | Playwright browser scenarios. |
| `deploy/` | Container and deployment configuration planned for M0.8. |
| `scripts/audit/` | Documentation index and codebase audit scripts (CI-checked). |
| `docs/` | Canonical architecture, security, sync, data model, decisions, facts, plans, mockups, and archive. See [`docs/README.md`](docs/README.md). |

## Local development

Use the .NET SDK specified in `global.json` and Node.js 24 with npm. Run the following commands from the repository root.

```powershell
dotnet restore Nexus.slnx
dotnet build Nexus.slnx --no-restore
dotnet test Nexus.slnx --no-build
npm.cmd --prefix src/Nexus.Client ci
```

Start the host in one terminal:

```powershell
dotnet run --project src/Nexus.Web --launch-profile http
```

Start the client in a second terminal at the repository root:

```powershell
npm.cmd --prefix src/Nexus.Client run dev
```

Open `http://127.0.0.1:5173`. The client retrieves the API version through the Vite proxy on port 5080. On Linux or macOS, use `npm` wherever the Windows instructions use `npm.cmd`.

## Publish and verify

```powershell
dotnet publish src/Nexus.Web -c Release -o artifacts/publish
Push-Location artifacts/publish
dotnet Nexus.Web.dll --urls http://127.0.0.1:5080
```

Stop the development host first if it is using port 5080. Publishing runs `npm ci`, builds the client, and places it in `wwwroot`. Open `http://127.0.0.1:5080` to check the integrated application. Stop the host with `Ctrl+C` and run `Pop-Location` to return to the repository root.

## Documentation index check

```powershell
pwsh -File ./scripts/audit/Build-KnowledgeIndex.ps1 -Check
```
