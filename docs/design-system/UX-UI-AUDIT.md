# Nexus UX/UI Audit

- Reviewed on 2026-11-23 using the [Render deployment](https://project-nexus-pqd8.onrender.com/) reporting API v1.0.0.
- Scope covered the public authentication screens and an authenticated browser smoke test using one test account.
- [Nexus Design System & UX Conventions](README.md) defines the product’s design and accessibility standards.

## Findings

| ID | Area | Finding | Review | Fix |
| --- | --- | --- | --- | --- |
| UXA-01 | Authentication | Left and Right Arrow do not navigate between the authentication tabs. | Reviewed | Ready for retest |
| UXA-02 | Group chat | Dismissing the creation dialog with Escape leaves keyboard focus on the page body. | Reviewed | Ready for retest |
| UXA-03 | Conversation files | The drawer briefly reports zero files while fetching a conversation’s existing files. | Reviewed | Ready for retest |

`Reviewed` means the finding was reproduced in the browser. Use `Needs review` for a new or unconfirmed report. Use `Open` for a fix still to be made, `Ready for retest` when a fix is available, and `Verified fixed` after repeating the check successfully. Record the retest date and environment on the finding.

### UXA-01 · Keyboard navigation on authentication tabs

1. On the sign-in screen, focus **Sign in** and press Right Arrow.
2. Select **Create account**, focus its tab, and press Left Arrow.

**Observed.** In both cases, focus and selection stayed on the current tab. Clicking either tab changed the form.

**Fix.** Added WAI-ARIA `ArrowLeft`/`ArrowRight`/`Home`/`End` keyboard navigation, roving `tabIndex`, and `aria-controls`/`tabpanel` bindings in `src/Nexus.Client/src/main.tsx`.

**Retest.** Verify that the arrow keys move focus between the tabs and that the selected tab and visible form follow the tab component’s activation behavior.

### UXA-02 · Focus after dismissing Create group chat

1. Sign in and open **Chat**.
2. Focus and activate **Group** to open **Create group chat**.
3. Press Escape.

**Observed.** The dialog closed and keyboard focus moved to the page body.

**Fix.** Captured the pre-open `document.activeElement` in shared `src/Nexus.Client/src/ui/Modal.tsx` and restored focus inside Radix `Dialog.Content` `onCloseAutoFocus` (covers all controlled modals across Chat and Communities).

**Retest.** Verify that Escape closes the dialog, returns focus to **Group**, and lets the next Tab continue from that control.

### UXA-03 · Empty state while loading conversation files

1. Open an existing conversation with a shared attachment.
2. Open **Files** and observe the initial state and the loaded state.

**Observed.** The drawer first displayed `Files (0)` and `No files shared in this conversation yet.` It then displayed `Files (1)` with the attachment.

**Fix.** Added explicit `loadingFiles` (and `loadingMembers`) state in `src/Nexus.Client/src/chat.tsx` and `src/Nexus.Client/src/communities.tsx` so drawers show a loading indicator instead of `0` / empty state while fetching.

**Retest.** Verify that the drawer indicates loading until the request finishes. A conversation with an attachment should display its file and count after loading. An empty conversation should display the empty state after loading.

## Smoke test coverage

| Flow | Review result |
| --- | --- |
| Sign-in validation | Empty fields prevented submission. Invalid credentials showed `Invalid username or password.` |
| Account creation | The form opened and displayed its fields and trial terms. Account creation is pending an end-to-end review. |
| Session | Valid sign-in worked. After signing out and reloading, the sign-in form reappeared. |
| Directory | An accented name search returned a match. A nonmatching query showed `No matching active accounts.` |
| Profile & Security | The page opened and displayed its settings and signed-in devices. Changes to those settings are outside this review. |
| Chat | An existing conversation opened and the app showed `Connected`. The composer enabled **Send** for a draft and disabled it once cleared. Two-account realtime delivery is pending review. |
| Group chat | The creation dialog opened and closed. Creating a group is pending an end-to-end review. |
| Files | An existing attachment appeared in the drawer after loading. Upload and download are pending an end-to-end review. |
