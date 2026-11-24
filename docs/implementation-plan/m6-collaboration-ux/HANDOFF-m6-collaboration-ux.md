# M6 — Collaboration UX Handoff

## M6.16 Chat and Files pagination

**Verified on November 24, 2026.** The Chat and Files lists use a timestamp and ID cursor and keep the 50-item page cap. The UI can load more even when a filter matches zero loaded conversations. Refresh revalidates the loaded pages, and transient failures leave loaded items available while offering retry. Personal Chat Files now resolve the null Organization scope through the same authorization helper as Organization Files.

```powershell
dotnet test tests/Nexus.IntegrationTests --filter 'FullyQualifiedName~PersonalChatAndFilesPagePastFifty|FullyQualifiedName~FilesM3Tests|FullyQualifiedName~MessagingM2Tests' --verbosity minimal
npm.cmd --prefix src/Nexus.Client run build
pwsh -File ./scripts/Run-Tests.ps1
git diff --check
```

The targeted tests passed 11 cases including tied page boundaries and access revocation between Files requests. The complete run passed 133 integration tests, 5 client tests, the web and Desktop builds, and the audits. In the isolated browser fixture the sidebar found a previously unloaded conversation while a filter was active, Files expanded from 50 to 52 rows, and a file from the last page downloaded through the Personal scope. Stopping and restarting the fixture host confirmed that a failed refresh kept the loaded files visible and **Retry** restored all 52 without leaving the conversation.

## M6.17 Drafts and send recovery

**Verified on November 24, 2026.** IndexedDB keeps drafts by account, scope, and conversation. Submitting atomically moves the draft into the original conversation's queue with its attachment references and idempotency key. Users can retry, edit, or dismiss failed sends. Attachment checks recognize expired or consumed uploads. Signing out clears local chat work and signs out the other tabs for the same account. Group removal clears the affected conversation's draft, queue, cursor, and history.

```powershell
npm.cmd --prefix src/Nexus.Client run build
dotnet build src/Nexus.Web -c Release --no-restore
dotnet test tests/Nexus.IntegrationTests --filter 'FullyQualifiedName~FilesM3Tests|FullyQualifiedName~MessagingM2Tests|FullyQualifiedName~ChatScopeTests' --verbosity minimal
pwsh -File ./scripts/Run-Tests.ps1
dotnet test tests/Nexus.IntegrationTests -c Release --filter 'FullyQualifiedName~FilesM3Tests' --verbosity minimal
git diff --check
```

The full run passed 133 integration tests, 5 client tests, the Release builds, and the audits. The targeted runs passed 16 and 4 tests. The Files cases cover the pending-ID lookup across uploaders and conversations, orphan cleanup, access revocation, and exact replay of a committed attachment-only send.

The isolated browser fixture restored independent chat drafts and their usable attachment chips after navigation and reload. Sending with an invalid body kept the failure in the original chat after switching away. Reloading preserved the failed entry, which could be edited and sent or dismissed. Stopping and restarting the fixture host queued and delivered one attachment message. Marking a staged fixture file unavailable preserved the draft text and required the file to be attached again. Removing the test member closed the chat and joining again showed an empty composer. Signing out cleared the other tab and a subsequent sign-in showed no old draft. The recovery actions fit at 320 px and 390 px without horizontal overflow, and Tab moved from **Retry** to **Edit draft**. The existing Activity view and attachment download link remained reachable.

## M6.18 Reading and scroll behavior

**Implemented and partially verified on November 24, 2026.** The browser fixture preserved the first visible message and its offset while receiving new messages and while recovering a missed message after a host restart. Prepending an older page kept the original message within 1 px. The **New messages** button could be activated with Enter and focused the newest message. While Communities was open, the server kept the previous read cursor and navigation showed one unread message. Opening the chat in a second tab read the message and removed the first tab's unread action without changing its scroll position.

```powershell
npm.cmd --prefix src/Nexus.Client run build
dotnet build src/Nexus.Web -c Release --no-restore
dotnet test tests/Nexus.IntegrationTests --filter 'FullyQualifiedName~M2_5_M2_7_SEC17_DeltaSync_Reconcile_410Gone_AndMonotonicReadState' --verbosity minimal
pwsh -File ./scripts/Run-Tests.ps1
git diff --check
```

The targeted test verified monotonic read updates, recovery, and preservation of the read cursor on a background SPA send. The complete run passed 133 integration tests, 5 client tests, the web and Desktop builds, and the audits.

The isolated browser event fixture simulated `visibilitychange` with `document.visibilityState` set to `hidden` while a new message was delivered over SignalR. The normal history endpoint returned read sequence 61 and newest sequence 62 while hidden. After the fixture restored visible state, both were 62 and the unread badge was gone. A native browser tab-switch check remains open. The in-app browser did not report a real hidden document after switching tabs. Native Chrome opened the fixture, but window input did not reach the sign-in form.

## M6.19 Composer IME and sizing

**Implemented and partially verified on November 24, 2026.** The composer honors native and tracked composition state as well as the IME key-code fallback. Unit tests cover those cases. In the isolated browser, the three simulated composition Enter events left the Vietnamese draft in the field and created no message. Shift+Enter added a line, plain Enter sent the multiline message, and **Send** sent the next message once. An operating-system IME session remains to be verified.

The redundant composer hints were removed. The field measured 36 px empty, 76 px with three lines, and 132 px with a long draft at 320 px, 390 px, and 1280 px viewport widths. Long drafts scrolled within the field. Clearing it restored 36 px, with no horizontal overflow. Older browsers have a measured-height fallback.

```powershell
npm.cmd --prefix src/Nexus.Client run test
npm.cmd --prefix src/Nexus.Client run build
git diff --check
```

## M6.20 Blocked users and Unblock

**Verified on November 24, 2026.** Chat uses the standard confirmation dialog and disables its composer when the caller has blocked the peer. **Manage blocked users** focuses the account-level list in Profile & security. The list supports paging, retries, and removal without exposing another account's block records. The conversation lookup also restores an eligible DM outside the loaded sidebar pages when it is selected from **New chat**.

```powershell
dotnet test tests/Nexus.IntegrationTests --filter 'FullyQualifiedName~PersonalConsentOrganizationInvitationAndRevocationEnforceScope|FullyQualifiedName~M2_11_BlockAndReportUser_EnforcedOnDmOpenAndSend' --verbosity minimal
dotnet build src/Nexus.Web -c Release --no-restore
pwsh -File ./scripts/Run-Tests.ps1
npm.cmd --prefix src/Nexus.Client run build
git diff --check
```

The targeted tests passed 2 cases, and the full run passed 133 integration tests, 7 client tests, the Release builds, and all audits. The browser opened a DM beyond the first 50 conversations, blocked the fixture peer, and showed the disabled composer. With the fixture host stopped, the settings list displayed a retry action. Restarting the host and retrying loaded the correct user, with the Unblock action inside a 320 px viewport. Unblocking returned the list to its empty state, and the original DM accepted one message. Integration coverage also verified that the blocker could see the block, the peer could not see it, and unblocking restored the accepted contact.

## M6.21 Group and Community ownership transfer

**Verified on November 24, 2026.** Both members drawers offer transfer actions to an Owner for eligible members on the loaded page. The confirmation shows the resource name and recipient account. Role and membership writes lock the scoped Group Chat or Community before checking the current roles, and a failed promotion rolls back the previous Owner's demotion. The UI refreshes after a successful or stale request, and the recipient's open drawer refreshes through SignalR. Member paging is tracked by M6.23.

```powershell
dotnet build src/Nexus.Web -c Release --no-restore
npm.cmd --prefix src/Nexus.Client run build
dotnet test tests/Nexus.IntegrationTests --filter 'FullyQualifiedName~M2_8_SEC18_GroupLifecycle|FullyQualifiedName~CommunitiesM5Tests|FullyQualifiedName~ChatScopeTests' --verbosity minimal
pwsh -File ./scripts/Run-Tests.ps1
git diff --check
```

The targeted run passed 10 tests. PostgreSQL tests exercised competing transfer requests, rejected revoked recipients and unauthorized actors, verified exactly one Owner after the competing requests, and rejected attempts to remove the new Owner. The full run passed 133 integration tests, 7 client tests, the Release builds, and the audits.

Two isolated browser accounts confirmed role refresh on both sides for a Group Chat and a Community. The Group dialog displayed the resource and target and could be cancelled before confirming. Removing the Community recipient while the dialog was open caused a clear error and kept the existing Owner. Re-adding the recipient allowed the transfer. The recipient gained the Owner action and the previous Owner lost it. At 320 px the dialog and both actions fit without horizontal overflow.

## M6.22 Operator-issued password reset

**Verified on November 24, 2026.** The signed-out **Reset password** form accepts the code, a new password, and confirmation. Unknown, expired, and used codes share the existing error response and a single recovery instruction. Codes remain in the form and request body. The backend locks the token row and commits redemption, password change, audit, and session revocation together.

```powershell
npm.cmd --prefix src/Nexus.Client run build
dotnet test tests/Nexus.IntegrationTests --filter 'FullyQualifiedName~IdentityM1aTests' --verbosity minimal
pwsh -File ./scripts/Run-Tests.ps1
git diff --check
```

The Identity suite passed all 5 cases, including invalid and expired codes, password policy rejection, exactly one successful redemption under concurrent requests, repeated redemption rejection, previous-session revocation, and signing in with the new password. The full suite passed 133 integration tests, 7 client tests, the Release builds, and the audits.

The browser walkthrough at 320 px verified entry from sign-in, focus on the code field, password mismatch feedback, rejection of a deliberately invalid code, a clean URL, field clearing when leaving the form, focus restoration, and keyboard navigation to the existing registration tab. Successful credential changes were exercised in the isolated HTTP integration suite.

## M6.23 People, Community, and feed pagination

**Verified on November 24, 2026.** All People, Community, member picker, workspace administration, and channel feed lists now enforce the 50-item page cap with opaque cursor or `after` pagination, loading/empty/error/retry states, and stale-response cancellation on scope switches. Added `nextCursor` pagination to `/api/people/directory` (including `excludeCommunityId` with `SEC-20b` Verified filtering for Official communities), `/api/personal/people/chat-candidates` (including `excludeConversationId`), `/api/org/departments`, `/api/communities` (member & Discover lists with `q`, `communityId`, `conversationId`), `/api/communities/{id}/members`, `/api/conversations/{id}/members`, and `/api/channels/{id}/posts` (preserving pinned-first ordering and tied-timestamp tie-breakers). Updated `PersonalConnections` contacts search to case-insensitive `ILike`, wired older feed post paging (**Load older posts**) in `communities.tsx` preserving loaded pages across background refreshes, converted `WorkspaceModals` and `ContactControls` in `organizationControls.tsx` to `useCursorList` + `ListPaging`, and reset department filters on Organization revocation in `main.tsx`.

```powershell
dotnet test tests/Nexus.IntegrationTests --filter 'FullyQualifiedName~PeopleCommunitiesMembersAndFeedsPagePastFiftyAcrossTwoOrganizations' --verbosity minimal
pwsh -File ./scripts/Run-Tests.ps1
git diff --check
```

The targeted PostgreSQL 17 integration test seeded populations over 50 across two isolated Organizations (including tied display names, tied department/community names, 3 pinned + 52 regular posts with tied timestamps, and 52 external contacts), verified exact page boundaries, server-side filters, `SEC-20b` Official eligibility, invalid-cursor rejection (`400`), and immediate `403` revocation between pages. The full verification pipeline passed 134 integration tests, 7 client unit tests, Release Web and Desktop builds (0 warnings/0 errors), security guardrails (163 files, 0 violations), UI design system compliance (33 files, 0 violations), and knowledge index checks. Live populated-screen browser walkthrough continues in `M6.24`.

## M6.24 Populated-screen UX retest

**Verified on November 24, 2026.** Fixed `UXA-13` (narrow Community feed header and mobile drawer responsive wrapping in `style.css`) and ran a live populated-screen browser retest against an isolated local PostgreSQL 17 + `Nexus.Web` host (`http://127.0.0.1:5190`) across desktop (`1440×900`), mobile (`390×844`), and narrow mobile (`320×700`) viewports in Light and Dark themes using both Owner (`uxowner01` / `Nguyễn Minh Anh`) and Member (`uxmember01` / `Trần Quang Huy`) accounts.

All ten outstanding findings (`UXA-04` through `UXA-13`) passed retest and were marked `Verified fixed` in `docs/design-system/UX-UI-AUDIT.md`. Three additional issues surfaced during the live walkthrough were fixed and re-verified on the rebuilt bundle:
1. Reconciled in-flight optimistic feed posts against SignalR `CommunityFeedChanged` refreshes via `inFlightPostIdsRef` in `communities.tsx` so publishing posts/announcements/polls leaves zero stale `"Publishing…"` duplicates.
2. Added ellipsis truncation on `.nx-conv-name > span` / `.nx-discover-head > .nx-conv-name` and `white-space: nowrap` on `.nx-discover-head > .nx-muted` in `style.css` so long community titles and member counts stay aligned.
3. Updated empty-state copy in read-only `AdminsOnly` channels (`communities.tsx`) to `"No posts in this channel yet."` when `canPost` is false.

```powershell
npm.cmd --prefix src/Nexus.Client run build
pwsh -File ./scripts/Run-Tests.ps1
git diff --check
```

## M6.25 Links to content

**Verified on November 24, 2026.** Added opaque scoped deep links (`/?link=<token>`) for chat messages, community feed posts, and channels/main feeds. Added `ContentLinkCodec` and `GET /api/links/resolve?target=...` (`Cache-Control: no-store`), enforcing current Organization/Community/Conversation membership, `VisibleFromSeq` (`SEC-18`), `SEC-20b` Official Verified checks, and returning a uniform non-disclosing `404 link_unavailable` for malformed, deleted, unauthorized, cross-scope, or revoked targets. Extended `GET /api/conversations/{id}/messages` with `aroundSeq`/`afterSeq` (`hasNewer`/`nextAfterSeq`) and `GET /api/channels/{id}/posts` with `aroundPostId` (`hasNewer`), both capped at 50 items, and included `targetLink` in `GET /api/notifications/{id}/source` so Activity reuses the same navigation. In the SPA (`contentLinks.ts`, `main.tsx`, `chat.tsx`, `communities.tsx`, `activity.tsx`), added **Copy link** buttons on messages, posts, and channels, same-origin in-app link interception, surrounding-context loading with **Jump to latest** / **Load newer messages**, visible `.nx-highlight-target` highlighting, and accessible programmatic focus.

```powershell
dotnet test tests/Nexus.IntegrationTests --filter 'FullyQualifiedName~ContentLinksResolveAcrossPersonalAndTwoOrganizationsWithContextAndNonDisclosure' --verbosity minimal
pwsh -File ./scripts/Run-Tests.ps1
git diff --check
```

The targeted PostgreSQL 17 integration test verified deep links across Personal scope (80 messages with `VisibleFromSeq` boundary) and two isolated Organizations (90 feed posts, Official vs Standard communities), surrounding-context paging (`<= 50`), non-disclosing `404` across all unauthorized/deleted/cross-org/revoked cases, and immediate revocation. The full pipeline passed 135 integration tests, 8 client unit tests, Release Web and Desktop builds, and all audits.

## M6.26 Sidebar organization

**Verified on November 24, 2026.** Added the `SidebarOrganization` EF Core migration (`user_sidebar_sections` and `user_sidebar_items`) and `/api/sidebar` CRUD + atomic reorder endpoints (`SidebarEndpoints.cs`), enforcing current membership checks on read and write plus immediate cleanup on group, community, and organization membership revocation. Added server-side `unreadOnly=true` filtering and `VisibleFromSeq`-scoped `lastMessagePreview` on `GET /api/conversations`, and member-only `latestPostPreview` on `GET /api/communities` (never exposed in Discover or to Unverified accounts in Official communities). Wired shared sidebar organization controls (`sidebarPrefs.ts`, `sidebarOrder.ts`, `sidebarControls.tsx`) into both `chat.tsx` and `communities.tsx` with Favorites, named sections (create/rename/delete), personal Hide/Show (preserving access, history, and notification preferences), **Unread** filter, message/post previews, keyboard Move up/down, and pointer drag-and-drop reordering.

```powershell
dotnet test tests/Nexus.IntegrationTests --filter 'FullyQualifiedName~SidebarOrganizationPersistsEnforcesScopePreviewsAndRevocation' --verbosity minimal
pwsh -File ./scripts/Run-Tests.ps1
git diff --check
```

The targeted PostgreSQL 17 integration test verified section creation/duplicate-409/rename/delete, favorites, reordering, hide/show preserving history & notification preferences, `VisibleFromSeq` preview isolation (`SEC-18`), Discover non-leakage, `unreadOnly` read-state updates, unauthorized `403`, and immediate removal across both sidebars upon group/org revocation. The full pipeline passed 136 integration tests, 9 client unit tests, Release Web and Desktop builds, and all audits.

## M6.27 Composer formatting, link insertion, and searchable Unicode emoji picker

**Verified on November 24, 2026.** Extended `markdown.ts` with safe unordered (`- ` / `* `) and ordered (`1. `) Markdown list rendering (`<ul/ol class="nx-md-list">`) while preserving `SEC-10` HTML escaping, `http:`/`https:`-only URL validation, and complex Unicode emoji (ZWJ, Fitzpatrick skin tones, variation selectors, flags). Added `composerFormatting.ts` (selection-preserving formatting, safe link insertion, emoji insertion, and IME-safe `Ctrl/Cmd+B/I/E/K` shortcuts), same-origin `emojiCatalog.ts` (400+ native Unicode entries across 9 categories with English keyword search and Fitzpatrick skin-tone modifiers, zero external CDN requests), lazy-loaded `EmojiPicker.tsx` (`React.lazy(() => import('./EmojiPicker'))` emitting an independent Vite chunk `EmojiPicker-*.js` loaded only when opened, with recents and skin-tone preferences persisted in `localStorage`), and reusable `ComposerToolbar.tsx` wired into both the Chat composer (`chat.tsx`) and Community feed post composer (`communities.tsx`) without disrupting `M6.17` IndexedDB drafts or `M6.19` IME composition.

```powershell
npm.cmd --prefix src/Nexus.Client run test
npm.cmd --prefix src/Nexus.Client run build
pwsh -File ./scripts/Run-Tests.ps1
git diff --check
```

The full pipeline passed 136 PostgreSQL 17 integration tests, 21 client unit tests (`markdown.test.mjs`, `composerFormatting.test.mjs`, `emojiCatalog.test.mjs`, plus existing suites), Release Web and Desktop builds (0 warnings/0 errors), security guardrails (180 files, 0 violations), UI design compliance (41 files, 0 violations), and knowledge index checks. Live Browser QA against an isolated local PostgreSQL 17 + `Nexus.Web` fixture (`http://127.0.0.1:5195`) with two real accounts (`qaalice27`, `qabob27`) confirmed zero external CDN requests, on-demand same-origin fetch of `EmojiPicker-*.js`, safe link rejection for `javascript:alert(1)`, Escape/Tab focus restoration, recents/skin-tone insertion, draft reload preservation, IME composition guard, cross-account formatted message and community post rendering, and zero horizontal overflow (`scrollWidth <= innerWidth`) across `1440×900`, `390×844`, and `320×700` in both Light and Dark themes with 0 console errors.

## M6.28 Accessible profile cards and timed availability states

**Verified on November 24, 2026.** Added the `UserTimedAvailability` EF Core migration (`availability_state` + nullable `availability_expires_at` with CHECK constraints on `users`), pure server-side `AvailabilityResolver` (UTC expiry resolution with zero background polling), and `ProfileAndAvailabilityEndpoints.cs` (`PUT /api/people/me/availability` with CSRF validation and immediate batched SignalR `AvailabilityChanged` fanout via `Clients.Groups(...)`; scope-isolated `GET /api/personal/people/{id}/profile-card`, `GET /api/organizations/{orgId}/people/{id}/profile-card`, and `GET /api/people/{id}/profile-card`; plus capped `<= 50` `/presence` snapshots). Coordinated `DoNotDisturb` with M4 `PushOutboxHandler` (suppressing Web Push while preserving Activity notifications and automatically resuming after expiry or clear) and client `suppressChime`. Wired `AvailabilityPicker`, `ProfileCardModal` (opening from avatars/names in Chat, Communities, and People with quick **Message** action), live `AvailabilityChanged` SignalR updates, and Light/Dark semantic `--status-*` tokens into `main.tsx`, `chat.tsx`, `communities.tsx`, `Avatar.tsx`, and `style.css`.

```powershell
dotnet test tests/Nexus.IntegrationTests --filter 'FullyQualifiedName~ProfileAndAvailabilityM628Tests' --verbosity minimal
npm.cmd --prefix src/Nexus.Client run test
pwsh -File ./scripts/Run-Tests.ps1
git diff --check
```

The targeted PostgreSQL 17 integration suite passed 4/4 tests covering Personal + 2 isolated Organizations, CHECK constraints, `nexus_runtime` DML, CSRF, timed expiry, multi-device and scoped SignalR `AvailabilityChanged` isolation/revocation, zero-leak Personal vs Organization profile cards, `SEC-20b`/block/Disabled enforcement, M4 `DoNotDisturb` push suppression + Activity preservation + post-expiry push resume, and `<= 50` presence caps. The full pipeline passed 140 integration tests, 26 client unit tests, Release Web and Desktop builds (0 warnings/0 errors), security guardrails (187 files, 0 violations), and UI design compliance (44 files, 0 violations). Live Browser QA with two real accounts (`qaalice28`, `qabob28`) verified timed `DoNotDisturb` / `Busy` persistence, scoped profile cards, quick **Message** navigation, Escape focus restoration, and caught & fixed a `320×700` Chat DM header truncation squeeze (re-verified clean across `1440×900`, `390×844`, and `320×700` in Light & Dark themes with 0 console/network errors).
