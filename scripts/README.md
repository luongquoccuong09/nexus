# Project Nexus Scripts

Standardized build, test, packaging, and audit scripts (modeled after `RevitR4Converter`).

## Quick Reference

| Script | Purpose | Example |
|---|---|---|
| `scripts/Run-Tests.ps1` | Full verification: security guardrails, knowledge index, Release build, PostgreSQL Testcontainers tests, client `SEC-10` XSS tests, Vite build, Desktop build | `pwsh -File .\scripts\Run-Tests.ps1` |
| `scripts/build/Build-ServerPackage.ps1` | Publishes single-origin `Nexus.Web` + SPA to `artifacts\server-package\` (+ optional Docker image) | `pwsh -File .\scripts\build\Build-ServerPackage.ps1 -BuildDockerImage` |
| `scripts/build/Build-DesktopInstaller.ps1` | Builds per-user Windows Desktop `.msi` (`WebView2` shell, Teams-style install with no admin UAC) into `artifacts\installer\` | `pwsh -File .\scripts\build\Build-DesktopInstaller.ps1 -ProductVersion 0.2.0 -ServerUrl "https://project-nexus.onrender.com" -NoPause` |
| `scripts/build/Build-All.ps1` | Runs `Run-Tests.ps1` + `Build-ServerPackage.ps1` + `Build-DesktopInstaller.ps1` end to end | `pwsh -File .\scripts\build\Build-All.ps1 -ProductVersion 0.2.0` |
| `scripts/build/Clean-BuildOutputs.ps1` | Cleans `artifacts/`, `bin/`, `obj/`, `dist/`, and generated WiX manifests | `pwsh -File .\scripts\build\Clean-BuildOutputs.ps1` |
| `scripts/audit/Test-SecurityGuardrails.ps1` | Static security check rejecting `AllowAnyOrigin`, disabled TLS validation, Supabase `:6543`, and client secrets | `pwsh -File .\scripts\audit\Test-SecurityGuardrails.ps1` |
| `scripts/audit/Test-UiDesignCompliance.ps1` | Automated UI design system audit (token-only colors, `Nexus` brand name, `SEC-10` safe HTML, icon button `aria-label`, `TrustBadge` usage) | `pwsh -File .\scripts\audit\Test-UiDesignCompliance.ps1` |
| `scripts/audit/Build-KnowledgeIndex.ps1` | Rebuilds or validates (`-Check`) `docs/decisions/INDEX.md` and `docs/facts/INDEX.md` | `pwsh -File .\scripts\audit\Build-KnowledgeIndex.ps1 -Check` |
| `scripts/deploy/Invoke-SupabaseMigrate.ps1` | Reads gitignored `.env` and runs EF Core `--migrate` and `--bootstrap-admin` against Supabase Session Pooler `:5432` | `pwsh -File .\scripts\deploy\Invoke-SupabaseMigrate.ps1` |
