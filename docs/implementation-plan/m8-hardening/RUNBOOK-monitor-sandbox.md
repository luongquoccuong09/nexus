# Monitor Sandbox Runbook

## Control deployment

Monitor is disabled by default. Apply the normal Nexus database migrations to the control deployment with its migration role. Configure the following keys in the control deployment's trusted configuration or secret store.

| Key | Purpose |
|---|---|
| `NexusMonitor__Enabled` | Set to `true` to expose Monitor. |
| `NexusMonitor__EnvironmentName` | Set a distinct deployment label such as `Development`, `Staging`, or `Production`. |
| `NexusMonitor__Readers__0` | The account UUID of an optional operator who can view metrics and reports. Increment the index for additional accounts. |
| `NexusMonitor__Operators__0` | The account UUID of an operator who can view and, with a verified MFA session, run and stop tests. |
| `NexusMonitor__RunnerKey` | A dedicated generated runner secret. Use 32 to 128 base64url characters. Keep it in the control deployment and on the runner host. |
| `NexusMonitor__LeaseSeconds` | Lease duration. Default 30 seconds and accepted range 15–120 seconds. |

The account UUID is the `id` returned by `GET /api/auth/me` when signed in as that account. Grant changes take effect according to the configuration provider. Restart after changing deployment environment variables. Organization roles and memberships have no effect on these grants. Use the existing Profile & security flow to enable TOTP and sign in with a verified MFA session.

Use an HTTPS control URL for remote operation, including Render. The worker initiates all coordination over outbound HTTPS. A Development control host can use loopback HTTP for local testing. Keep the production runner key and production database/storage credentials out of local sandbox configuration.

## Runner host

Install Docker with Compose and the Nexus .NET SDK. From the repository root, build an approved local image. When building a clean checkout, pass the checked-out commit SHA to populate the report. Dirty working-tree tests can omit the label and still record the immutable image ID.

```powershell
$commit = git rev-parse HEAD
docker build -f deploy/Dockerfile --build-arg "NEXUS_COMMIT=$commit" -t nexus-monitor:local .
docker pull postgres:17-alpine

$env:NEXUS_RUNNER_CONTROL_URL = 'http://127.0.0.1:5080'
$env:NEXUS_RUNNER_IMAGE = 'nexus-monitor:local'
# Populate NEXUS_RUNNER_KEY from the trusted secret store.
dotnet run --project src/Nexus.LoadRunner
```

The worker uses `deploy/docker-compose.monitor.yml` and writes leased run state to `artifacts/monitor-runner`. Trusted host configuration can override these locations with `NEXUS_RUNNER_COMPOSE_FILE` and `NEXUS_RUNNER_STATE_DIR`. Run a single worker per state directory. Protect that directory and keep it across restarts so the worker can clean up and resubmit a result for a recovered lease.

In Nexus, open **Monitor**, select **Load Test**, choose **60 users** and **Docker Sandbox · Capacity**, then select **Run Load Test**. Switch to **Performance** and select **Docker Sandbox** to observe the target. Run history and both exports are available to users with Monitor access. Operators can select **Stop** from Run status. Use comparably configured reports to establish a baseline.

- Capacity uses isolated sandbox-only limits of 24,000 requests per minute and 600 sign-ins per minute from the runner address. Policy fidelity uses the application defaults. Compare like profiles.
- The web container uses 2 vCPU and 768 MiB. PostgreSQL has its own limits. PostgreSQL and the target web container join an internal network. A fixed-upstream transport relay publishes a dynamic localhost port so the worker can reach the web container on Docker Desktop. The database and the target web container have no host-published ports.
- Provisioning uses a new database, normal migration role, synthetic accounts and local storage. A 300-user run seeds 1,000 Community members while opening at most 300 virtual-user connections.

## Stop and recovery

Stop in Nexus first. If the control deployment is unavailable, create the `emergency-stop` marker inside the configured runner state directory or interrupt the runner at its terminal. The marker stops the current run and prevents new claims until the operator removes it. A loss of control heartbeat also expires the local lease and stops workload execution. When restarting after an interrupted run, preserve the state directory and let the worker reconcile its run before accepting another.

The worker checks target readiness, memory pressure and sustained request failures. Reports retain a terminal reason and cleanup state. A failed cleanup retains the active slot so a replacement test cannot reuse unverified resources. The worker retries cleanup and result delivery from its local state.

## Telemetry and historical scope

The Performance view reads short in-memory samples for the current deployment and recent runner snapshots for the selected sandbox. A restart discards those chart samples. PostgreSQL retains run summaries and lifecycle events.

For durable telemetry, configure `NexusObservability__ExportMetrics=true` and the standard `OTEL_EXPORTER_OTLP_ENDPOINT` and authentication settings on the observed deployment. Point the exporter at the approved OTLP receiver. Use a Prometheus-compatible store and Grafana for long windows and alert evaluation. The external health check should run independently of Nexus. Host network, host storage, DAU/WAU, provider-specific storage, remote staging execution and independent pilot alert policies require their own data sources and validation. The Monitor UI shows missing sources as unavailable.
