# M4 — Notifications and Outbox Handoff

## M4.1 Transactional outbox

**Verified on November 24, 2026.** `POST /api/organizations/{organizationId}/posts/{postId}/remind` saves its existing audit record and the outbox event together. The handler re-evaluates active Organization and Community membership, official designation, and acknowledgment before creating an in-app notification. The existing HTTP status and response shape are preserved. The visible Activity view is tracked by `M4.3`.

The background worker claims one due event per transaction with `FOR UPDATE SKIP LOCKED`. A retry rolls back handler writes first. Retry delays are 5, 10, 20, 40, and 80 seconds, with the sixth failed attempt dead-lettered. Unknown event kinds are dead-lettered on the first attempt. Database effects and completion normally commit together. A unique key on the outbox ID and recipient prevents duplicate notifications during event replay. The worker logs event identifiers and failure types.

```powershell
dotnet ef migrations add NotificationOutbox --project src/Nexus.Web --output-dir Infrastructure/Data/Migrations
dotnet test tests/Nexus.IntegrationTests --filter 'FullyQualifiedName~OutboxM4Tests|FullyQualifiedName~CommunitiesM5Tests' --verbosity minimal
dotnet ef migrations has-pending-model-changes --project src/Nexus.Web --no-build
pwsh -File ./scripts/Run-Tests.ps1
git diff --check
```

The focused suites passed 4 tests using PostgreSQL 17. They exercised the real reminder HTTP route and hosted worker, authorized recipients, replay, locked rows, rollback after a handler wrote data, retry timing, and dead-letter transitions. The full verification run passed 123 integration tests, 3 client tests, the production client build, the Desktop build, and all three audits. EF reported zero pending model changes. Browser UI acceptance remains open for the full M4 and M6 delivery.

## M4.2 Mentions

**Verified on November 24, 2026.** Scope-qualified messages, Community posts, and comments resolve `@username` against eligible participants. Broadcasts require `MentionChannelOrAll`. Tokens lacking broadcast permission remain text and generate no broadcast notification. The event stores recipient identifiers in its originating transaction. The worker checks current eligibility and source visibility before creating a notification. The separate unread mention badge uses `/api/notifications/mentions/unread` and chat read cursors.

```powershell
dotnet ef migrations add MentionOutboxRecipients --project src/Nexus.Web --output-dir Infrastructure/Data/Migrations
dotnet test tests/Nexus.IntegrationTests --filter 'FullyQualifiedName~PersonalMentionsDeduplicate|FullyQualifiedName~OutboxM4Tests|FullyQualifiedName~CommunitiesM5Tests' --verbosity minimal
pwsh -File ./scripts/Run-Tests.ps1
dotnet ef migrations has-pending-model-changes --project src/Nexus.Web --no-build
git diff --check
```

The full suite passed 124 integration tests, 3 client tests, the production client build, the Desktop build, and all three audits. Cases cover case-insensitive deduplication, message retry, ignored code and URL tokens, read cursors, out-of-scope participants, and authorized and unauthorized broadcasts. The in-app browser at `http://127.0.0.1:5186/` used the isolated `nexus-goal-validation-db` container and synthetic accounts. It showed one unread mention alongside one unread message, opened the Direct Message and empty Files drawer, and displayed the Community post. Opening the chat cleared its message badge while the Community mention remained. Narrow Community header clipping is recorded under `UXA-13` for `M6.24`.

## M4.3 Activity

**Verified on November 24, 2026.** Activity reads paged, recipient-owned results and resolves source visibility on each request. The read endpoint requires an antiforgery token and preserves the original read timestamp on repeated calls. Message read cursors and announcement acknowledgments are reflected in the unread filter. Source previews use the shared sanitized renderer.

```powershell
dotnet test tests/Nexus.IntegrationTests --filter 'FullyQualifiedName~PersonalMentionsDeduplicate|FullyQualifiedName~M5_2_M5_3_OfficialDesignation' --verbosity minimal
npm.cmd --prefix src/Nexus.Client run build
pwsh -File ./scripts/audit/Test-UiDesignCompliance.ps1
pwsh -File ./scripts/Run-Tests.ps1
git diff --check
```

The first focused run exposed a test helper attempting to parse JSON from an expected bodyless `404`. After correcting that assertion, the full suite passed 124 integration tests, 3 client tests, the production client build, the Desktop build, and the audits. The integration tests cover outsider source access, revocation, stable pagination across tied timestamps, and idempotent mark-as-read. On the isolated loopback fixture, the in-app browser reloaded the new bundle, opened Activity, read the source preview, followed it to the Community feed, marked the item as read, and showed an empty Unread-only view with the mention badge cleared.

## M4.4 Notification preferences

**Verified on November 24, 2026.** Personal Chat, Organization conversations, and Community feeds share a per-account preference endpoint with current resource authorization. Mode defaults to All activity and respects the legacy muted flag when no explicit preference exists. Notification handlers apply the mode at dispatch time. The daily quiet-hours schedule stores local start and end times with a time zone. The delivery stages in `M4.5` and `M4.6` use this schedule for external notifications.

```powershell
dotnet ef migrations add NotificationPreferences --project src/Nexus.Web --output-dir Infrastructure/Data/Migrations
pwsh -File ./scripts/Run-Tests.ps1
git diff --check
```

The full run passed 130 integration tests, 3 client tests, production client and Desktop builds, and all three audits. Tests exercise per-account preference isolation, authorization, muted delivery, persisted quiet hours, and the fall daylight-saving transition. The loopback browser saved and reopened an overnight Asia/Bangkok schedule, persisted Mentions only for the Community feed and Muted for the Direct Message, and rendered the narrow Community header without the earlier clipping at the observed width.

## M4.5 Web Push

**Verified locally on November 24, 2026.** Activity exposes browser opt-in using a same-origin service worker and PWA manifest. ASP.NET Core uses P-256 VAPID signing and empty HTTP push requests. A stable Topic coalesces requests per conversation and the worker uses a generic Notification API tag. Expired endpoints are removed. The sender rechecks unread status, source access, device session, preferences, and quiet hours before transmission. See the [configuration and iOS installation guide](../../MAINTAINER_GUIDE.md#web-push-setup-and-ios-installation).

```powershell
dotnet ef migrations add WebPushSubscriptions --project src/Nexus.Web --output-dir Infrastructure/Data/Migrations
dotnet test tests/Nexus.IntegrationTests --filter 'FullyQualifiedName~WebPushDelivers|FullyQualifiedName~NotificationQuietHoursTests|FullyQualifiedName~PersonalMentionsDeduplicate' --verbosity minimal
dotnet ef migrations has-pending-model-changes --project src/Nexus.Web --no-build
pwsh -File ./scripts/Run-Tests.ps1
npm.cmd --prefix src/Nexus.Client run test
npm.cmd --prefix src/Nexus.Client run build
git diff --check
```

The focused PostgreSQL and HTTP suites passed 8 cases. The test push service verified the VAPID signature and audience, stable Topic, empty body, expiration handling, and cancellation of deferred delivery after the message was read. The full verification run passed 131 integration tests, both production builds, and the audits. The subsequent client suite passed 5 tests, including generic service-worker display and click behavior. The loopback browser loaded Activity with **Enable push** ready. The manifest, worker, icons, and Activity start link returned `200` with their expected content types. Live delivery on deployment origins and iOS devices remains part of the pilot deployment check.

## M4.6 Email digest

**Verified locally on November 24, 2026.** Opt-in is private to the account and requires a confirmed account email or a verified Organization employee address. A scheduled digest selects currently unread, eligible notifications without an accepted push. It honors conversation mode and quiet hours, groups up to 50 items, and sends a generic count using the SMTP STARTTLS path when configured. The worker records the items included to avoid resending them when later jobs run. See the [SMTP setup and operational boundary](../../MAINTAINER_GUIDE.md#optional-email-digest).

```powershell
dotnet ef migrations add EmailDigest --project src/Nexus.Web --output-dir Infrastructure/Data/Migrations
dotnet ef migrations has-pending-model-changes --project src/Nexus.Web --configuration Release --no-build
pwsh -File ./scripts/Run-Tests.ps1
git diff --check
```

The full run passed 132 integration tests, 5 client tests, both production builds, and all audits. The recorded mail transport verified that only the unread item without accepted push appeared in the digest and that later jobs sent no duplicate. The isolated browser verified saving and reopening the opt-in and turning it off again. Live SMTP relay delivery is a deployment check.
