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

## M6.13 Direct voice and video calls

Verified November 25, 2026. The full pipeline passed 209 PostgreSQL 17 integration tests, 62 client unit tests, the security and UI audits, and Web and Desktop Release builds. One opt-in Monitor test was skipped. The six-test Calls and Meetings slice included simultaneous responses, Personal and Organization isolation, session-bound media, expiry, and revocation after blocking.

Eligible Personal DMs and authorized Organization DM contexts offer Voice call and Video call. SignalR propagates a versioned state change to both accounts' signed-in devices. Acceptance stops ringing everywhere and only the initiating and accepting sessions can join the media room. Decline, cancel, timeout, and end clear the corresponding prompts. Participants can share a screen or end the call.

The additive `20261125102457_DirectCalls` migration applies the scope and account foreign keys and enforces one ringing or accepted call per DM. A caller-scoped request ID deduplicates retries, and a locked call row serializes acceptance with other transitions. Expiration records a single missed call. Access and media admission are rechecked against current account, session, membership, and DM block state. The Supabase Postgres Best Practices skill was reviewed before the migration.

Two separate browser sessions per account verified the six viewport and theme combinations at `1440×900`, `390×844`, and `320×700`. Keyboard initiation, ringtone dismissal on the other device, missed-call expiry, an Organization DM, video and screen sharing, and microphone permission denial and recovery passed with zero unexpected browser or network errors. Audible ringing in a fresh tab requires the browser's first in-app interaction. The visible incoming prompt works before that interaction.

In the final loopback run, two voice calls took 944 and 581 ms, and one video call took 665 ms from the answer click to inbound audio on both devices. The accepted call loads the deferred media module without waiting for the Suspense fallback to reveal the room. Authorized devices prepare audio alongside signaling and publish it before video. Vite emitted 556.78 kB of deferred room JavaScript (147.65 kB gzip). Remote latency remains part of the target deployment gate.

The scheduled meeting, Chat Meet now, and Community Meet now browser regression also passed on the final build. Confirming End for everyone now disconnects the host before the server revokes the host credential. This prevents an unnecessary host reconnect during closure. The regression and direct-call matrices ran against fresh loopback host processes because the two harnesses share a source IP and together exceeded the host's request quota.

```powershell
dotnet ef migrations has-pending-model-changes --project src/Nexus.Web/Nexus.Web.csproj --startup-project src/Nexus.Web/Nexus.Web.csproj --context NexusDbContext --configuration Release --no-build
dotnet test tests/Nexus.IntegrationTests/Nexus.IntegrationTests.csproj --configuration Release --artifacts-path 'D:/develop-nexus-m610-jitsi/m613-test-release-artifacts' --filter 'FullyQualifiedName~DirectCallsM613Tests|FullyQualifiedName~MeetingsM612Tests' --logger 'console;verbosity=minimal' --nologo
pwsh -File ./scripts/Run-Tests.ps1
git diff --check
```

The four-device, two-account Edge fixture and its logs are in `D:\develop\.nexus-m610-eval\m613`. It uses the normal Nexus host, PostgreSQL 17, and the local LiveKit evaluation container.

```powershell
$callsQa = 'D:\develop\.nexus-m610-eval\m613'
dotnet publish src/Nexus.Web -c Release --artifacts-path "$callsQa\artifacts" -p:SkipClientPublish=true -o "$callsQa\web"
npm.cmd --prefix src/Nexus.Client run build
Copy-Item -Path 'src\Nexus.Client\dist\*' -Destination "$callsQa\web\wwwroot" -Recurse -Force
pwsh -File "$callsQa\start-host.ps1" -Migrate
pwsh -File "$callsQa\start-host.ps1" *> "$callsQa\web.log"
# In another shell, with the host running
node "$callsQa\calls-qa.mjs"
# Restart the loopback host and keep its output in web.log before running this matrix.
node "$callsQa\scheduled-meetings-qa.mjs"
```

## M6.14 Recurrence and iCalendar export

Verified November 25, 2026. The full pipeline passed 214 PostgreSQL 17 integration tests, 65 client unit tests, the security and UI audits, and Web and Desktop Release builds. One opt-in Monitor test was skipped. The EF model matched the last migration.

Calendar supports daily, weekly, and monthly series, with an optional inclusive end date in the event's time zone. The organizer can change or cancel a single occurrence or the series. The original occurrence instant identifies a moved event in the Calendar, in `.ics`, and in meeting admission. Each occurrence of an online series gets its own room. Series-level editing and RSVP retain the existing organizer and attendee access rules.

The additive `20261125152704_CalendarRecurrence` migration records the series rule, indexed occurrence exceptions, occurrence reminder metadata, and a separate identity for scheduled occurrence rooms. The list cursor keeps actual start, event ID, and original occurrence in order, and the date window stays capped at 62 days. The outbox stages the next eligible occurrence reminder as it processes the current one. An unchanged reminder is deduplicated, while Activity can open the occurrence that triggered it. The Supabase Postgres Best Practices skill was reviewed before the migration.

iCal.NET `5.2.4` serializes the series with a stable UID, the scheduling time zone, exclusions for canceled occurrences, and detached overrides for edits. The focused DST probe verified skipped spring-gap occurrences, the later physical instant in the fall overlap, and preservation of a one-hour duration through export and import. The PostgreSQL 17 suite covered Personal and two Organizations, Community revocation, 51 occurrences across a tied page boundary, concurrency, revoked credentials, and reminder replay.

```powershell
dotnet run --project D:/develop/nexus-m614-recurrence-probe/Probe.csproj --artifacts-path D:/develop/nexus-m614-recurrence-probe/artifacts
dotnet test tests/Nexus.IntegrationTests/Nexus.IntegrationTests.csproj --configuration Release --artifacts-path 'D:/develop-nexus-m610-jitsi/m614-test-release-artifacts' --filter 'FullyQualifiedName~RecurringCalendarM614Tests|FullyQualifiedName~CalendarM611Tests|FullyQualifiedName~MeetingsM612Tests' --logger 'console;verbosity=normal' --nologo
dotnet ef migrations has-pending-model-changes --project src/Nexus.Web/Nexus.Web.csproj --startup-project src/Nexus.Web/Nexus.Web.csproj --context NexusDbContext --configuration Release --no-build
pwsh -File ./scripts/Run-Tests.ps1
git diff --check
```

Separate Edge contexts in `America/New_York` and `Europe/London` passed at `1440×900`, `390×844`, and `320×700` in Light and Dark. Both accounts saw five daily occurrences at the correct UTC offsets across the New York DST boundary. The organizer moved and canceled individual occurrences and canceled the series. The attendee downloaded `.ics` with matching UID, recurrence ID, and exclusion. Keyboard operation, one-off event creation, and clearing the recurrence rule also passed. The final run reported zero unexpected browser or network errors. Scratch files and logs are in `D:\develop\.nexus-m610-eval\m614`.

```powershell
$recurrenceQa = 'D:\develop\.nexus-m610-eval\m614'
dotnet publish src/Nexus.Web -c Release --artifacts-path "$recurrenceQa\artifacts" -p:SkipClientPublish=true -o "$recurrenceQa\web"
npm.cmd --prefix src/Nexus.Client run build
Copy-Item -Path 'src\Nexus.Client\dist\*' -Destination "$recurrenceQa\web\wwwroot" -Recurse -Force
pwsh -File "$recurrenceQa\start-host.ps1" -Migrate
pwsh -File "$recurrenceQa\start-host.ps1" *> "$recurrenceQa\web.log"
# In another shell, with the host running
node "$recurrenceQa\recurrence-qa.mjs"
```

## M6.15 Release verification

Local verification completed November 26, 2026. The full pipeline passed 214 PostgreSQL 17 integration tests, 65 client unit tests, the security and UI audits, and Web and Desktop Release builds. One opt-in Monitor test was skipped. The roadmap retains a staging checkbox for the target deployment gate below.

The local validation uses the built SPA, the normal Nexus.Web application, PostgreSQL 17, and a single-node LiveKit provider behind the private signaling gateway. The browser matrix uses two accounts in independent Edge contexts at `1440×900`, `390×844`, and `320×700` in Light and Dark. The two-account Desktop runs use the Release `ProjectNexus` executable and WebView2 on a private Windows desktop with an isolated profile.

The browser matrix covered Calendar and RSVP, recurring events, scheduled meetings, Chat and Community Meet now, and direct calls across concurrent devices. The scheduled meeting fixture held the confirmed end request, pressed Escape, and verified that the command completed and closed the room. The direct call fixture verified microphone denial and retry, video, synthetic tab sharing, and ring dismissal on both accounts' other sessions.

The Desktop matrix exercised Calendar and Activity, recurrence and `.ics`, scheduled and immediate meetings, and direct calls with a separate Edge participant. The actual WebView2 runtime was `158.0.4370.33`. A private test profile denied and recovered camera and microphone permissions using synthetic hardware. Edge decoded the screen share of the generated window on the private desktop. Desktop also restored bidirectional RTP on the original page 2,205 ms after a signaling interruption. A block from a second login closed sockets and tracks in 482 ms. The isolation, permission, and final network runs recorded zero unexpected diagnostics.

The Windows verification uses the process-scoped `NEXUS_DESKTOP_DATA_ROOT` option with a fresh absolute profile path. That option scopes the WebView2 history, configuration, and singleton for the test process. The launcher creates a private Windows desktop and a generated screen-sharing window. It verifies that the foreground desktop stays active throughout the test.

The final isolated Desktop build measured 982 ms for voice and 614 ms for video from the answer click to bidirectional inbound audio. The four-device Edge matrix measured 610 and 613 ms on the later samples and 1,673 ms on the first sample while the Desktop matrix was also running. An authorized incoming ring now starts loading the deferred media module. The browser confirmed the chunk was absent from startup and that the ringing tab held zero media peer connections before acceptance.

The network fixture on Edge `158.0.4370.33` joined two unrelated private scheduled rooms. Each participant roster contained one account and neither room received a remote track. An unrelated account's join returned `404`, and presenting a grant from the wrong BFF session returned `401`. The fixture interrupted one active media signaling socket. Nexus displayed Reconnecting and returned to Connected without a reload in 2,321 ms, with bidirectional audio continuing. Blocking the other account from a second signed-in session closed both peers' sockets and capture in 793 ms. Both prior grants returned `401`, replacement admission returned `404`, and meeting discovery returned the neutral unavailable response. The final run had zero unexpected diagnostics.

The local capacity probe used headless Edge `158.0.4370.33`, LiveKit `1.13.11`, and Docker Desktop `29.9.3` on an Intel Core i7-8700 host with 12 logical CPUs. Each provider container had a two-CPU, 512 MiB limit. Samples used synthetic 640×360 video at 12 fps with simulcast disabled. Docker CPU percentages use one CPU as the 100% reference. Interface counters supply the receive and send rates.

| Selected transport and topology | Sustained inbound tracks | Time to media for all clients | Resource and traffic sample |
|---|---:|---:|---|
| UDP video, three rooms with 4 + 2 + 2 accounts | 16 audio and 16 video | 2.190 s | Median CPU 25.08%, peak 27.68%. RAM 84.65–85.57 MiB. Receive 2.032 Mb/s, send 3.924 Mb/s over 19.41 s. |
| UDP voice, four concurrent two-account calls | 8 audio | 1.777 s | Median CPU 15.14%, peak 17.03%. RAM 72.03–72.88 MiB. Receive 0.847 Mb/s, send 0.831 Mb/s over 15.30 s. |
| Direct ICE/TCP video, two accounts with UDP disabled in the test provider | 2 audio and 2 video | 1.111 s | Median CPU 5.76%, peak 6.25%. RAM 60.68–61.52 MiB. Receive 0.546 Mb/s, send 0.537 Mb/s over 11.58 s. |

The largest exercised room held four participants. Four one-to-one voice calls ran concurrently. These measurements establish a local lower bound. Staging should use the expected participant mix, network paths, and soak duration.

```powershell
$networkQa = 'D:/develop/.nexus-m610-eval/m615-network'
dotnet test tests/Nexus.IntegrationTests/Nexus.IntegrationTests.csproj -c Release --no-restore --filter FullyQualifiedName~PersonalAndCommunityMeetNowFollowLiveConversationMembership
dotnet publish src/Nexus.Web/Nexus.Web.csproj -c Release --no-restore -p:SkipClientPublish=true -o "$networkQa/web"
npm.cmd --prefix src/Nexus.Client run build
Copy-Item -Path 'src\Nexus.Client\dist\*' -Destination "$networkQa/web/wwwroot" -Recurse -Force
pwsh -NoProfile -File "$networkQa/start-host.ps1" -WebRoot "$networkQa/web"
# In a separate shell while the host runs
node "$networkQa/network-qa.mjs"

$capacityQa = 'D:/develop/.nexus-m610-eval/m615-capacity'
pwsh -NoProfile -File "$capacityQa/Start-ProbeProviders.ps1"
node "$capacityQa/capacity.mjs" video-udp
node "$capacityQa/capacity.mjs" voice-udp
node "$capacityQa/capacity.mjs" video-tcp
```

```powershell
$releaseQa = 'D:/develop/.nexus-m610-eval/m615'
dotnet publish src/Nexus.Web -c Release --artifacts-path "$releaseQa/artifacts" -p:SkipClientPublish=true -o "$releaseQa/web"
npm.cmd --prefix src/Nexus.Client run build -- --outDir "$releaseQa/client"
New-Item -ItemType Directory -Force "$releaseQa/web/wwwroot" | Out-Null
Copy-Item -Path "$releaseQa/client/*" -Destination "$releaseQa/web/wwwroot" -Recurse -Force
pwsh -File "$releaseQa/start-host.ps1" *> "$releaseQa/web.log"
# In a separate shell while the host runs
node "$releaseQa/calendar-qa.mjs"
node "$releaseQa/recurrence-qa.mjs"
node "$releaseQa/scheduled-meetings-qa.mjs"
node "$releaseQa/calls-qa.mjs"
```

```powershell
$desktopQa = 'D:/develop/.nexus-m610-eval/m615-desktop'
$desktopExe = 'D:/develop/nexus-workplace/src/Nexus.Desktop/bin/Release/net10.0-windows/ProjectNexus.exe'
dotnet build src/Nexus.Desktop/Nexus.Desktop.csproj -c Release
dotnet build "$desktopQa/DesktopQaLauncher.csproj" -c Release -o "$desktopQa/launcher-native"
pwsh -File 'D:/develop/.nexus-m610-eval/m614/start-host.ps1' -WebRoot "$releaseQa/web" *> "$releaseQa/web-desktop.log"
# Use a fresh profile for each independent group. Run the launcher in a separate shell.
& "$desktopQa/launcher-native/DesktopQaLauncher.exe" $desktopExe "$desktopQa/private-media" 18753 --native-permissions
& "$desktopQa/launcher-native/DesktopQaLauncher.exe" $desktopExe "$desktopQa/private-calendar" 18754
node "$desktopQa/permissions-cdp.mjs"
node "$desktopQa/scheduled-meetings-qa.mjs"
node "$desktopQa/network-qa.mjs"
node "$desktopQa/calendar-qa.mjs"
node "$desktopQa/recurrence-qa.mjs"
node "$desktopQa/calls-qa.mjs"
pwsh -File ./scripts/Run-Tests.ps1
git diff --check
```

### Target deployment gate

Local measurements cover the loopback Docker and browser configuration. Complete the staging gate when the target deployment and a second test network are accessible.

1. Deploy the approved single-node LiveKit version in the pilot region with a TLS-valid private signaling and administration origin. Verify that the external network reaches the configured ICE endpoints and Nexus media gateway.
2. Join with two test accounts on different network paths. Capture selected ICE candidate types and protocols for the direct UDP path. Repeat from a managed VPN or UDP-restricted corporate path and verify the selected TURN/TLS relay.
3. Exercise scheduled meetings, Meet now, direct voice and video, and screen sharing in the browser and Windows Desktop. Test reconnect and revoke room access while media is active. Verify denial of both fresh admission and previously issued credentials after revocation.
4. Measure stable room size and concurrent-call capacity for the target participant mix. Record answer latency, RTP loss and jitter, CPU, memory, and network and relay egress. Use the M8.2 metrics and agreed pilot thresholds to identify the supported limits.
5. Attach staging results and deployment settings to the M8 review package before enabling meetings or calls for the pilot.
