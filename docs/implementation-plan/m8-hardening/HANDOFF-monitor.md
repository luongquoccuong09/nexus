# Monitor sandbox handoff

This slice implements deployment-scoped Monitor access, same-origin Performance and Load Test views, the independent .NET runner, Docker sandbox provisioning, bounded aggregate telemetry, history, and JSON/HTML export. The [sandbox runbook](RUNBOOK-monitor-sandbox.md) describes operator configuration and recovery.

The baseline preset is 60 users for 15 minutes with a 3-minute ramp. Each virtual user uses its own BFF cookie and the official .NET SignalR client. The benchmark measures WebSocket-backed delivery to expected connected recipients and reconciles outstanding deliveries against the history API. It records steady-phase throughput and distribution percentiles. The control host owns the benchmark result and versions its criteria.

## Verification commands

Run from the repository root.

```powershell
dotnet build Nexus.slnx -v minimal
dotnet test Nexus.slnx --no-build -v minimal
npm.cmd --prefix src/Nexus.Client run build
npm.cmd --prefix src/Nexus.Client test
pwsh -File scripts/audit/Test-SecurityGuardrails.ps1
pwsh -File scripts/audit/Test-UiDesignCompliance.ps1
pwsh -File ./scripts/audit/Build-KnowledgeIndex.ps1
pwsh -File scripts/audit/Build-KnowledgeIndex.ps1 -Check
dotnet ef migrations has-pending-model-changes --project src/Nexus.Web --no-build
docker build -f deploy/Dockerfile -t nexus-monitor:verify-final .
$env:NEXUS_MONITOR_SMOKE_IMAGE = 'nexus-monitor:verify-final'
dotnet test tests/Nexus.IntegrationTests --no-build --filter FullyQualifiedName~MonitorTests -v minimal
```

The Docker smoke exercises three runs through a real control API and a dedicated local worker. It checks separate deployment grants, MFA, antiforgery, idempotency, machine leases, real SignalR sockets and message delivery, uploads, reconnect and offline synchronization, cancellation, reports, and removal of the Compose project's containers, volumes, and networks. The last run checks local shutdown after the control API disappears. The real runner tests are opt in so normal integration runs do not depend on a prebuilt Nexus image. The final targeted run passed both Monitor tests. The whole solution verification passed 174 tests with the Docker smoke run separately. The frontend passed 33 tests.

## Browser and benchmark verification

A local browser run used the default 60-user capacity preset and completed after 900 seconds with `Pass`. It recorded 9,875 successful HTTP operations, zero failures and HTTP 429 responses, 3,239 messages, and 94,761 realtime deliveries. The run recorded zero missing or duplicate messages. Steady p95 was 27.4 ms for reads, 34.5 ms for direct and group sends, and 34.5 ms for realtime delivery. The approved local image was built from a working tree. Its report identifies the image digest and application version. Treat this as one local sample rather than a repeatable baseline on the future IT host.

Browser verification covered main navigation authorization, both Monitor tabs, Light and Dark themes, survival after a page reload, live sandbox charts, a second run stopped from the UI, and comparison of two runs. Both reports for the first run were retrieved from the authorized export endpoints.

## Scope of the remaining roadmap gates

- `M8.2` remains open for repeat baseline runs, a verified 300-socket and reconnect benchmark, and the specific 1,000-member workload and p95 acceptance criteria.
- `M8.5` remains open for independent alerts and the host, backup, scanner, and storage sources in the pilot deployment. Optional OTLP export is available.
- `M7.6` remains open for DAU/WAU, storage and push adoption. Monitor shows available committed business counters and unavailable sources explicitly.
