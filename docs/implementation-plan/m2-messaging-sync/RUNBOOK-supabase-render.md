# Runbook — Deploying Project Nexus on Supabase + Render

- Target architecture: **Supabase** (PostgreSQL 17 via Session Pooler `:5432` + Private File Storage) + **Render** (Docker container from [`deploy/Dockerfile`](../../../deploy/Dockerfile) with automatic HTTPS).
- Cost: Free tier for demo/testing (Render Free sleeps after 15 minutes of inactivity; upgrade to Render Starter or Railway for always-on).

## Step 1 — Initialize Supabase (Database + Private Storage)

1. In [Supabase](https://supabase.com), create a new project in region **Southeast Asia (Singapore)**.
2. Open **SQL Editor**, paste [`deploy/supabase/init-supabase.sql`](../../../deploy/supabase/init-supabase.sql), replace both placeholder passwords (`REPLACE_WITH_STRONG_MIGRATOR_PASSWORD` and `REPLACE_WITH_STRONG_RUNTIME_PASSWORD`), and click **Run**.
3. Open **Storage → New bucket**:
   - Name: `nexus-files`
   - **Public bucket:** **OFF** (must remain private; Nexus authorizes every download server-side).
4. Copy these values from **Project Settings**:
   - **Database → Connect → Session Pooler (port `5432`)** — NEVER use Transaction Pooler port `6543`.
     - Migrator connection string:
       `Host=aws-0-ap-southeast-1.pooler.supabase.com;Port=5432;Database=postgres;Username=nexus_migrator.<PROJECT_REF>;Password=<MIGRATOR_PASSWORD>;SSL Mode=Require`
     - Runtime connection string:
       `Host=aws-0-ap-southeast-1.pooler.supabase.com;Port=5432;Database=postgres;Username=nexus_runtime.<PROJECT_REF>;Password=<RUNTIME_PASSWORD>;SSL Mode=Require`
   - **API → Project URL** (`https://<PROJECT_REF>.supabase.co`) and **service_role secret key** (server-side only; never place in `src/Nexus.Client/`).

## Step 2 — Run EF Core migrations & bootstrap first SystemAdmin (from your workstation)

Run from the repository root (`d:\develop\nexus-workplace`):

```powershell
$env:ConnectionStrings__Migration = "Host=<POOLER_HOST>;Port=5432;Database=postgres;Username=nexus_migrator.<PROJECT_REF>;Password=<MIGRATOR_PASSWORD>;SSL Mode=Require"
dotnet run --project src/Nexus.Web -- --migrate

$env:ConnectionStrings__Runtime = "Host=<POOLER_HOST>;Port=5432;Database=postgres;Username=nexus_runtime.<PROJECT_REF>;Password=<RUNTIME_PASSWORD>;SSL Mode=Require"
$env:BootstrapAdmin__Username = "nexus_sysadmin"
$env:BootstrapAdmin__Password = "Choose-A-Strong-Admin-Password-2026!"
$env:BootstrapAdmin__DisplayName = "System Admin"
dotnet run --project src/Nexus.Web -- --bootstrap-admin
```

Or copy `.env.example` to `.env` (gitignored) and run:

```powershell
pwsh -File .\scripts\deploy\Invoke-SupabaseMigrate.ps1
```

Clear temporary shell secrets afterwards:

```powershell
Remove-Item Env:ConnectionStrings__Migration, Env:ConnectionStrings__Runtime, Env:BootstrapAdmin__Username, Env:BootstrapAdmin__Password, Env:BootstrapAdmin__DisplayName -ErrorAction SilentlyContinue
```

## Step 3 — Deploy `Nexus.Web` on Render

1. Push your repository to GitHub/GitLab.
2. In [Render](https://render.com), click **New → Blueprint** and select this repository. Render automatically reads [`render.yaml`](../../../render.yaml).
3. Fill in the three secret environment variables prompted by Render:
   - `ConnectionStrings__Runtime` (using `nexus_runtime.<PROJECT_REF>` on **port 5432** with `SSL Mode=Require`)
   - `NexusStorage__SupabaseUrl` (`https://<PROJECT_REF>.supabase.co`)
   - `NexusStorage__SupabaseServiceRoleKey` (server-side secret key)
4. Wait for the Docker build to succeed and `/healthz` to turn green.
5. Open `https://<your-service>.onrender.com`:
   - Verify `/api/version` returns JSON.
   - Sign in as `admin`, enroll TOTP MFA under **Security**, or let colleagues register directly on the **Register** tab.

## Step 4 — Build the Windows Desktop `.msi` pointing at your Render URL

```powershell
pwsh -File .\scripts\build\Build-DesktopInstaller.ps1 -ProductVersion 0.2.0 -ServerUrl "https://<your-service>.onrender.com" -NoPause
```

Distribute `artifacts\installer\ProjectNexus-0.2.0-x64.msi` (and its `.sha256` file) to colleagues. When installed, Project Nexus opens directly to your Render server — no admin privileges required (`perUser` install like Microsoft Teams).
