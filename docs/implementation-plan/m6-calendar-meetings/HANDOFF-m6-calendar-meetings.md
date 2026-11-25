# M6 Calendar and Meetings Handoff

## M6.10 component evaluation

Verified November 25, 2026, on `develop` from baseline `39dfff6`. The [component decision](../../decisions/2026-11-25-m6-d01-calendar-and-media-components.md) selects FullCalendar Standard, iCal.NET, and single-node LiveKit behind Nexus-controlled signaling admission.

### Calendar and recurrence

FullCalendar Standard React `7.1.5` passed keyboard traversal and event activation for month, week, and agenda at `1440×900`, `390×844`, and `320×700` in Light and Dark. The twelve local starts took 338–860 ms from keyboard activation to visible event, including harness time. All six layout configurations fit the available width. The calendar JavaScript and CSS were requested on open, and the browser harness recorded zero page or HTTP errors. The prototype used fixture events.

iCal.NET `5.2.4` preserved 09:00 local in `America/New_York` across the March and November clock changes. It applied a moved occurrence and a canceled occurrence, excluded the endpoint of a bounded query, and preserved stable UID and time-zone data after `.ics` serialization and reload. The library accepted an IANA identifier resolved from the corresponding Windows time zone.

```powershell
$calendarProbe = Join-Path ([IO.Path]::GetTempPath()) 'nexus-m610-calendar-eval'
Set-Location (Join-Path $calendarProbe 'browser')
npx vite build --manifest
npx vite preview --host 127.0.0.1 --port 18718 --strictPort
# In another shell, with the preview running
Set-Location (Join-Path $calendarProbe 'browser')
node verify.mjs
npm audit --omit=dev
Set-Location (Join-Path $calendarProbe 'IcalProbe')
dotnet run --configuration Release
dotnet list package --include-transitive --vulnerable
```

### Media

LiveKit `1.13.11` reported single-node routing with no Redis. The Windows package was checked against the upstream SHA-256 before running it on loopback. In Edge `158.0.4370.33`, two separate browser contexts with simulated devices passed five voice and five video runs. The inviter joined an empty room first. The answer timing started when the second client requested its credential and ended when both clients reported subscriptions. The harness separately confirmed incoming microphone bytes and decoded camera frames where applicable.

| LiveKit loopback measurement | Voice | Video |
|---|---:|---:|
| Initial join, range and median | 181–441 ms, 188 ms | 181–521 ms, 183 ms |
| Answer to second client connected, range and median | 186–199 ms, 196 ms | 180–200 ms, 186 ms |
| Answer to both clients subscribed, range and median | 345–377 ms, 360 ms | 379–605 ms, 575 ms |

A production-build comparison started from the second client's click and included downloading the dynamically imported SDK and waiting for incoming microphone bytes and decoded camera frames on both clients. LiveKit's five results were 718, 823, 883, 815, and 899 ms, with a median of 823 ms. The initial join including SDK load took 253–744 ms. Vite emitted a 2.57 kB startup JavaScript file (1.23 kB gzip) and a separate 546.77 kB media chunk (144.36 kB gzip).

These timings cover media connection with a test credential endpoint. Nexus ringing, authorization, a production signaling path, and Internet or TURN latency remain unmeasured. The LiveKit room service returned success for participant removal and the active client reported a server removal disconnect. An isolated signaling proxy then tied the signed room and participant claims to a per-session grant. It revoked the grant before asking LiveKit to remove the participant. The provider removed the active participant, the proxy rejected the previously issued credential with `401`, the fixture rejected replacement issuance with `403`, and the organizer stayed connected. The production integration still needs the same checks with durable authorization and private provider signaling.

After the Docker recovery, the test filtered UDP at the dedicated LiveKit container's media port. Both browser clients exchanged audio and video in all five runs. LiveKit reported TCP for all ten participant connections, and the answer-to-subscription intervals were 511–833 ms. This qualifies local ICE/TCP when UDP is unavailable. An external VPN, TURN/TLS, and WAN capacity still need staging evidence.

Docker recorded 0.03% CPU and 15.46 MiB after a clean start. Three samples while two clients published synthetic microphone and camera streams showed 10.63–11.00% CPU and 58.17–59.82 MiB. For the native Windows build, `Get-Process` sampled 108.2 MiB working set and 0% of one CPU core over 1.8 seconds at idle. With the same two-client workload, the sample was 112.3 MiB and 11.1% of one core over 3.5 seconds. These are spot samples, and the Docker and Windows memory metrics measure different things.

The pinned WinForms WebView2 SDK `1.0.3179.45` with runtime `158.0.4370.33` exchanged microphone and camera media with Edge. Compact mode retained track IDs and connection time while media continued. A host-canceled capture and denied device request each produced `NotAllowedError`. Retrying with permitted devices recovered. An explicit Share screen action fired `CoreWebView2.ScreenCaptureStarting`, and Edge decoded video from the isolated test window after automatic source selection. A separate two-origin fixture confirmed that camera, mic, and display capture in an iframe required explicit delegation. Physical camera, microphone, and manual source selection remain pending.

```powershell
docker pull livekit/livekit-server:v1.13.11
$liveKitProbe = 'D:\develop\.nexus-m610-eval\livekit'
Set-Location $liveKitProbe
# Three shells for the native provider, Vite fixture, and Edge harness
& .\native\livekit-server.exe --config .\livekit-native.yaml
$env:NEXUS_PROBE_SIGNAL = 'http://127.0.0.1:17980'
& .\node_modules\.bin\vite.cmd --port 17970
$env:NEXUS_PROBE_WEB = 'http://127.0.0.1:17970'
node probe.mjs video
node probe.mjs voice
# Production-bundle lazy SDK comparison, using build and preview in a separate shell
& .\node_modules\.bin\vite.cmd build --config vite.lazy-preview.config.mjs
& .\node_modules\.bin\vite.cmd preview --config vite.lazy-preview.config.mjs
node lazy-probe.mjs
$env:NEXUS_PROBE_WEB = 'http://127.0.0.1:17870'
$env:NEXUS_HOLD_PROBE = '1'
node capacity-spot-check.mjs video
# While the harness holds the room open
docker stats --no-stream --format '{{.CPUPerc}} {{.MemUsage}} {{.NetIO}}' nexus-m610-livekit
# Two shells for the isolated signaling proxy and its revocation check
& .\node_modules\.bin\vite.cmd build --config vite.guard-preview.config.mjs
& .\node_modules\.bin\vite.cmd preview --config vite.guard-preview.config.mjs
node guard-probe.mjs
# Two more shells for the WebView2/Edge qualification
dotnet run --project D:/develop/nexus-m610-desktop-probe/MediaProbe.csproj --no-build -c Release -- http://127.0.0.1:17970/ --auto-screen
node D:/develop/nexus-m610-desktop-probe/livekit.mjs --screen
```

The UDP qualification used a second LiveKit container listening on signaling `17890`, ICE/TCP `17891`, and ICE/UDP `17892`. It published the two TCP ports to loopback. Within that test container's network namespace, a disposable helper joined with `--network container:nexus-m610-livekit-tcp --cap-add NET_ADMIN` and installed `iptables` drop rules for inbound UDP destination port `17892` and outbound UDP source port `17892`. The Edge harness then ran `node probe.mjs video` with `NEXUS_PROBE_WEB=http://127.0.0.1:17871`, and the provider logs confirmed the selected transport.

Jitsi Docker bundle `stable-11509-1` deployed successfully after the Docker disk move. The Edge harness started its answer timer just before the second client's click and included IFrame API download and creation. P2P reached bidirectional audio/video in 4,592, 4,706, and 4,642 ms. Five SFU samples using a private adapter for UDP took 9,602, 3,978, 4,019, 4,462, and 3,703 ms. The default loopback-only candidate initially signaled successfully and required the private adapter for RTP. The same iframe continued carrying media after compact resize. Edge reported active sharing of a synthetic browser tab. Its UDP policy flag left the candidate using UDP, so that run provides no fallback evidence. The four-container idle sample was 485.07 MiB and 0.28% CPU. A two-client SFU snapshot reached 644.03 MiB and 96.17% CPU during startup, and a P2P sample reached 608.77 MiB and 6.28% CPU. Startup and concurrent activity limit these CPU comparisons.

The JWT checks rejected a guest and an otherwise signed credential for an unrelated room. A valid participant joined. The moderator removed that participant, who could still rejoin using the previously issued credential against the default self-hosted stack. The hidden Jitsi WebView2 fixture showed a decoded 1280 px pre-join camera preview, exchanged microphone bytes and decoded video with Edge, preserved the same iframe while media counters increased in compact mode, and completed leave with zero page errors. The explicit share action triggered `ScreenCaptureStarting` for the cross-origin frame. The hidden fixture left the source picker unattended.

```powershell
$jitsiSetup = 'D:/develop-nexus-m610-jitsi/setup'
$jitsiCompose = Join-Path $jitsiSetup 'docker-jitsi-meet-stable-11509-1'
docker compose --project-name nexus-m610-jitsi --project-directory $jitsiCompose up -d
# Start the local fixture in a separate shell when needed
python "$jitsiSetup/probe.py"
$env:JITSI_EVAL_SETUP = $jitsiSetup
node 'D:/develop-nexus-m610-jitsi/measure.mjs' bridge 5
node 'D:/develop-nexus-m610-jitsi/measure.mjs' p2p 5
node 'D:/develop-nexus-m610-jitsi/measure.mjs' auth
node 'D:/develop-nexus-m610-jitsi/measure.mjs' udp-blocked 1
node 'D:/develop-nexus-m610-jitsi/measure.mjs' screen-share 1
docker stats --no-stream nexus-m610-jitsi-web-1 nexus-m610-jitsi-prosody-1 nexus-m610-jitsi-jicofo-1 nexus-m610-jitsi-jvb-1
# Hidden WebView2 and headless Edge, in separate shells
dotnet build D:/develop/nexus-m610-desktop-probe/MediaProbe.csproj -c Release -o D:/develop/nexus-m610-desktop-probe/hidden-media
dotnet D:/develop/nexus-m610-desktop-probe/hidden-media/MediaProbe.dll http://localhost:18745/
node D:/develop/nexus-m610-desktop-probe/jitsi-media.mjs
```

### Repository verification and continuation

The first full `Run-Tests.ps1` run failed at the PostgreSQL 17 Testcontainers stage when Docker's data disk became read-only. The integration runner reported 121 passed, 67 failed, and 1 skipped. Failures included migration connections closing and container startup or cleanup timing out. The C: drive had under 200 MB free during the incident. The operator moved Docker Desktop's data disk through its settings to `D:\DockerDesktopData`. Its engine and existing volumes were visible after the move, and the existing Compose PostgreSQL service completed crash recovery and reported healthy. The repeat full run passed security and UI audits, solution Release build, 188 integration tests, 53 client unit tests, client production build, and Windows Desktop Release build. One opt-in Monitor test was skipped.

```powershell
pwsh -File ./scripts/audit/Build-KnowledgeIndex.ps1
pwsh -File ./scripts/Run-Tests.ps1
npm.cmd --prefix src/Nexus.Client run test
npm.cmd --prefix src/Nexus.Client run build
dotnet build src/Nexus.Desktop -c Release
git diff --check
```

M6.12 must verify the production path for provider isolation and current Nexus grant checks before enabling real media. The M6.15 release gate includes Internet or staging TURN/TLS and target deployment capacity.

## M6.11 Calendar and RSVP

Verified November 25, 2026. The full pipeline passed 203 PostgreSQL integration tests, 56 client unit tests, security and UI audits, and the Web and Desktop Release builds. One opt-in Monitor test was skipped. The five Calendar integration tests covered Personal and two Organization scopes, Community revocation, concurrent edits, 51-item event and attendee pagination, DST offsets, and reminder authorization and deduplication.

The Calendar destination loads FullCalendar Standard on first open. It offers Personal and Organization calendars with a Community filter, Month, Week, and Agenda views, and event creation, editing, cancellation, invitations, and RSVP. Activity opens calendar notifications directly in an authorized event dialog. The production build emitted 330.19 kB of Calendar JavaScript (93.44 kB gzip) and 22.67 kB of Calendar CSS (4.87 kB gzip). Both assets were absent from the startup trace and requested on open.

The additive `20261125041156_CalendarEvents` migration was applied to a PostgreSQL 17 database with the previous `develop` schema. It adds the composite range index, scope and attendee foreign keys, and a Personal- and Organization-scoped creation key. Event changes and their M4 outbox entries commit together. The worker rechecks eligibility, suppresses expired reminders, and deduplicates deliveries. Activity rechecks access and hides superseded reminders. The Supabase Postgres Best Practices skill was reviewed before the migration.

Two authenticated Edge contexts in `America/New_York` and `Europe/London` passed the browser matrix at `1440×900`, `390×844`, and `320×700` in Light and Dark. Both accounts used keyboard navigation to reach Calendar and switch views. The runs covered creating a Personal event, matching local display to a shared UTC instant, inviting an eligible contact, all three RSVP states, Community calendar access, opening an event from Activity, updating and canceling an event. They recorded zero unexpected browser or HTTP errors and no page-width overflow.

```powershell
dotnet ef migrations has-pending-model-changes --project src/Nexus.Web --configuration Release --no-build
npm.cmd --prefix src/Nexus.Client run build
node --test src/Nexus.Client/tests/calendar-time.test.mjs
dotnet test tests/Nexus.IntegrationTests -c Release --filter 'FullyQualifiedName~CalendarM611Tests|FullyQualifiedName~AuthorizationMatrixTests' --verbosity minimal
dotnet test tests/Nexus.IntegrationTests/Nexus.IntegrationTests.csproj --configuration Release --artifacts-path 'D:/develop-nexus-m610-jitsi/m611-test-release-artifacts' --filter FullyQualifiedName~CalendarM611Tests --logger 'console;verbosity=minimal' --nologo
pwsh -File ./scripts/Run-Tests.ps1
git diff --check
```

The isolated browser harness used the built SPA with the normal `Nexus.Web` host on loopback and PostgreSQL 17 with separate migration and runtime roles. Its scratch files and observations are under `D:\develop\.nexus-m610-eval\m611`.

```powershell
$calendarQa = 'D:\develop\.nexus-m610-eval\m611'
dotnet publish src/Nexus.Web -c Release --no-restore -p:SkipClientPublish=true -o "$calendarQa\web"
Copy-Item -Path 'src\Nexus.Client\dist\*' -Destination "$calendarQa\web\wwwroot" -Recurse -Force
pwsh -File "$calendarQa\start-host.ps1" -Migrate
pwsh -File "$calendarQa\start-host.ps1"
# In another shell, with the host running
node "$calendarQa\calendar-qa.mjs"
```

## M6.12 Scheduled meetings and Meet now

Verified November 25, 2026. The full pipeline passed 206 PostgreSQL integration tests, 59 client unit tests, security and UI audits, and Web and Desktop Release builds. One opt-in Monitor test was skipped.

Calendar events can be marked as online meetings. Eligible users can join those events or start and join an active Meet now room from Chat or a Community channel. The media room loads the approved LiveKit SDK on demand. Participants control their microphone, camera, and screen sharing. The host can remove a participant or end the room for everyone.

The additive `20261125050605_MeetingRooms` migration creates room, grant, ban, and provider cleanup state. Each initial credential lasts 60 seconds. Nexus verifies the BFF session, active account, resource access, and grant on every signaling connection. Host removal and access revocation invalidate grants before contacting LiveKit. Reconciliation retries pending provider changes. WebView2 uses the default consent prompt and source picker for the configured server origin. The Desktop tray provides a user-triggered reset for the server's camera and microphone permissions.

For a loopback Docker deployment, configure one LiveKit API key and a secret of at least 32 bytes in `NEXUS_MEETINGS_LIVEKIT_KEY` and `NEXUS_MEETINGS_LIVEKIT_SECRET`. Then run the compose overlay. It starts the pinned single-node LiveKit image in the Web container's network namespace. Signaling and administration bind to loopback inside that namespace. The overlay publishes only the local ICE/TCP and ICE/UDP media ports in addition to the Nexus Web port.

```powershell
docker compose -f deploy/docker-compose.dev.yml -f deploy/docker-compose.meetings.dev.yml config --quiet
docker compose -f deploy/docker-compose.dev.yml -f deploy/docker-compose.meetings.dev.yml up --build
```

For remote deployments use an HTTPS private provider origin for `NexusMeetings:LiveKit:ServerUrl`, a dedicated provider key, and no publicly routed provider signaling or Twirp endpoints. Exclude `/api/media` query strings from ingress logs. Configure the separate ICE endpoints for the target network and qualify TURN/TLS during release verification.

The PostgreSQL 17 integration tests covered Personal and Organization scope, Community and account revocation, event cancellation, session-bound signaling, host-only controls, active eviction, and HTTP and WebSocket denial of a revoked credential. The browser harness used two real BFF accounts, the actual `Nexus.Web` host, and single-node LiveKit `1.13.11`. Both accounts received microphone packets and decoded camera frames at `1440×900`, `390×844`, and `320×700` in Light and Dark. It also exercised a simulated camera permission denial and retry, a synthetic tab screen share, Chat and Community Meet now, leaving, ending, and removal. The final run recorded zero unexpected browser or HTTP errors and found none of its issued credentials in the Nexus server log. The media JavaScript and CSS remain deferred. Vite emitted 554.97 kB of room JavaScript (147.08 kB gzip) and 1.21 kB CSS (0.51 kB gzip).

```powershell
dotnet ef migrations has-pending-model-changes --project src/Nexus.Web/Nexus.Web.csproj --startup-project src/Nexus.Web/Nexus.Web.csproj --context NexusDbContext --no-build
dotnet test tests/Nexus.IntegrationTests/Nexus.IntegrationTests.csproj --configuration Release --artifacts-path 'D:/develop-nexus-m610-jitsi/m612-test-release-artifacts' --filter FullyQualifiedName~MeetingsM612Tests --logger 'console;verbosity=minimal' --nologo
pwsh -File ./scripts/Run-Tests.ps1
git diff --check
```

The browser fixture and logs are in `D:\develop\.nexus-m610-eval\m612`. It used the existing isolated PostgreSQL 17 database and the loopback LiveKit evaluation container.

```powershell
$meetingsQa = 'D:\develop\.nexus-m610-eval\m612'
dotnet publish src/Nexus.Web -c Release --artifacts-path "$meetingsQa\artifacts" -p:SkipClientPublish=true -o "$meetingsQa\web"
npm.cmd --prefix src/Nexus.Client run build
Copy-Item -Path 'src\Nexus.Client\dist\*' -Destination "$meetingsQa\web\wwwroot" -Recurse -Force
pwsh -File "$meetingsQa\start-host.ps1" -Migrate
pwsh -File "$meetingsQa\start-host.ps1" *> "$meetingsQa\web.log"
# In another shell, with the host running
node "$meetingsQa\meetings-qa.mjs"
```
