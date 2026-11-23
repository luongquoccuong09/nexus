# Nexus UX/UI Audit

- Latest review on 2026-11-23 after the Communities deployment. [Render](https://project-nexus-pqd8.onrender.com/) reported API `v1.0.0` and served `/assets/index-gXtmKNM2.js`.
- Browser review covered authentication, Chat, Directory, the Communities landing and Discover screens, and the Create community dialog. Viewports included 1440 × 900, 390 × 844, and 320 × 700 CSS pixels.
- The test account had no Community memberships and Discover returned zero results. A disposable local fixture also stopped at browser sign-in because the local session cookie did not persist. Populated feeds, Channels, and role-specific actions remain pending live review.
- [Nexus Design System & UX Conventions](README.md) defines the product’s design and accessibility standards.

## Enterprise UX benchmark

**6.0 / 10** for the reviewed screens. This is a weighted design assessment. Teams and Slack are references for comparable workflows using their official documentation. The assessment covers interface quality and excludes feature breadth and formal accessibility conformance. A score of 5 indicates that common tasks work with visible friction. A score of 8 indicates consistent, predictable behavior across the reviewed journeys and release checks.

| Dimension | Weight | Score | Evidence |
| --- | ---: | ---: | --- |
| Visual alignment and component consistency | 25% | 6.0 | Chat has an inset header and aligned controls. Communities starts flush with the sidebar edge, and Directory uses mixed control heights in one row. |
| UI copy and terminology | 20% | 5.5 | The rail, screen title, and accessible names diverge. `New DM`, `Group`, `colleagues`, and `Discover public` are less explicit than the tasks they represent. |
| Navigation and action hierarchy | 20% | 5.5 | Community actions appear in three places. Starting a direct message opens Directory without focusing search. |
| Keyboard and accessibility behavior | 20% | 7.0 | Auth tabs and dialog focus restoration passed retest. Main navigation still lacks a programmatic current item and several visible labels differ from their accessible names. |
| Responsive layout and feedback | 15% | 6.0 | Directory reflowed at 320 px and the Community dialog fit at 390 px. The desktop trial notice clips its dismiss control, and the narrow Chat header gives substantial space to secondary actions. |

- **Microsoft Teams.** Its documented chat and channel flows give users a named compose action, a recipient entry point, and keyboard navigation through conversations. Apply that clarity to the Nexus chat entry point and Community hierarchy. [Teams chat and channels](https://support.microsoft.com/en-us/teams/teams-channels/explore-the-new-chat-and-channels-experience-in-microsoft-teams) · [Teams keyboard and screen reader flows](https://support.microsoft.com/en-us/accessibility/teams/basic-tasks-using-a-screen-reader-with-microsoft-teams)
- **Slack.** Its documented sidebar organizes Channels and direct messages, and its compose entry is distinct from navigation to an existing conversation. Apply that distinction to the Nexus rail and sidebar. Slack uses `DMs` in its own interface. The expanded Nexus wording is a product copy decision for this audience. [Slack direct messages](https://slack.com/help/articles/212281468-Understand-direct-messages) · [Slack sidebar](https://slack.com/help/articles/212596808-Adjust-your-sidebar-preferences)

## Findings

| ID | Priority | Area | Finding | Review | Fix |
| --- | --- | --- | --- | --- | --- |
| UXA-01 | P1 | Authentication | Left and Right Arrow did not navigate between the authentication tabs. | Reviewed | Verified fixed |
| UXA-02 | P1 | Group chat | Dismissing the creation dialog with Escape left keyboard focus on the page body. | Reviewed | Verified fixed |
| UXA-03 | P2 | Conversation files | The drawer briefly reported zero files while fetching existing files. | Reviewed | Verified fixed |
| UXA-04 | P1 | Main navigation | Visible and accessible labels disagree, and the current destination is conveyed visually only. | Reviewed | Ready for retest |
| UXA-05 | P1 | UI copy | `colleague` and `colleagues` appear in core task labels and instructions. | Reviewed | Ready for retest |
| UXA-06 | P1 | Chat | `New DM` opens Directory without focusing search, and `Group` obscures the create action. | Reviewed | Ready for retest |
| UXA-07 | P2 | Communities layout | The sidebar title and toolbar sit flush against the rail and use a separate row from the title. | Reviewed | Ready for retest |
| UXA-08 | P1 | Communities actions | Create and Discover repeat at three entry points on the same desktop screen, and Discover has inconsistent labels. | Reviewed | Ready for retest |
| UXA-09 | P2 | Directory layout | Search is 31 px tall beside 40 px filters and buttons in the desktop toolbar. | Reviewed | Ready for retest |
| UXA-10 | P1 | Environment notice | The trial notice clips its text and dismiss control on desktop and is hidden at 390 px. | Reviewed | Ready for retest |
| UXA-11 | P2 | Narrow Chat | Persistent Files, Report, and Block buttons crowd the conversation header at 320 px. | Reviewed | Ready for retest |
| UXA-12 | P1 | Community visibility | The default `Public` option describes its audience indirectly and does not explain what people can do with that visibility. | Reviewed | Ready for retest |

`Reviewed` means the finding was reproduced in the browser. Use `Needs review` for a new or unconfirmed report. Use `Open` for a fix still to be made, `Ready for retest` when a fix is available, and `Verified fixed` after repeating the check successfully. Record the retest date and environment on the finding.

P1 affects a common task, accessibility, or a consequential product choice. P2 covers visual consistency or secondary interaction friction. Suggested implementation order is UXA-04 through UXA-06, then UXA-07, UXA-08 and UXA-12 together, followed by the notice and remaining layout work. Use the normal review and release process for each change.

### UXA-01 · Keyboard navigation on authentication tabs

1. On the sign-in screen, focus **Sign in** and press Right Arrow.
2. Select **Create account**, focus its tab, and press Left Arrow.

**Observed.** In both cases, focus and selection stayed on the current tab. Clicking either tab changed the form.

**Fix.** Added WAI-ARIA `ArrowLeft`/`ArrowRight`/`Home`/`End` keyboard navigation, roving `tabIndex`, and `aria-controls`/`tabpanel` bindings in `src/Nexus.Client/src/main.tsx`.

**Retest.** Verify that the arrow keys move focus between the tabs and that the selected tab and visible form follow the tab component’s activation behavior.

**Result.** Passed on Render on 2026-11-23. Right Arrow selected **Create account**, and Left Arrow returned to **Sign in**.

### UXA-02 · Focus after dismissing Create group chat

1. Sign in and open **Chat**.
2. Focus and activate **Group** to open **Create group chat**.
3. Press Escape.

**Observed.** The dialog closed and keyboard focus moved to the page body.

**Fix.** Captured the pre-open `document.activeElement` in shared `src/Nexus.Client/src/ui/Modal.tsx` and restored focus inside Radix `Dialog.Content` `onCloseAutoFocus` (covers all controlled modals across Chat and Communities).

**Retest.** Verify that Escape closes the dialog, returns focus to **Group**, and lets the next Tab continue from that control.

**Result.** Passed on Render on 2026-11-23 for Escape dismissal and focus restoration to **Group**. The key immediately after focus restoration remains part of the full keyboard acceptance check.

### UXA-03 · Empty state while loading conversation files

1. Open an existing conversation with a shared attachment.
2. Open **Files** and observe the initial state and the loaded state.

**Observed.** The drawer first displayed `Files (0)` and `No files shared in this conversation yet.` It then displayed `Files (1)` with the attachment.

**Fix.** Added explicit `loadingFiles` (and `loadingMembers`) state in `src/Nexus.Client/src/chat.tsx` and `src/Nexus.Client/src/communities.tsx` so drawers show a loading indicator instead of `0` / empty state while fetching.

**Retest.** Verify that the drawer indicates loading until the request finishes. A conversation with an attachment should display its file and count after loading. An empty conversation should display the empty state after loading.

**Result.** Passed on Render on 2026-11-23 for the original reproduction. A conversation with an attachment displayed `Files` and a loading state first, then `Files (1)` with the attachment. A genuinely empty conversation remains part of the full acceptance check.

### UXA-04 · Navigation names and current destination

**Observed.** The rail shows **Teams**, **People**, and **Security**, while its accessible names are **Communities**, **Directory**, and **Profile and Security**. At 390 px, the selected destination has a visual highlight and the navigation buttons expose no current or selected state.

**Fix.** Standardized rail labels, accessible names, and topbar titles to **Chat**, **Communities**, **Directory**, **Profile & security**, and **HR import**, and added `aria-current="page"` on the active navigation item in `src/Nexus.Client/src/main.tsx` and `src/Nexus.Client/src/style.css`.

**Retest.** Use **Chat**, **Communities**, **Directory**, and **Profile & security** consistently when displaying text, naming controls, and titling destinations. Use an icon with an accessible name and a keyboard-accessible tooltip when space requires an icon-only control. Verify that the current destination is announced and remains visually distinct. [W3C Label in Name](https://www.w3.org/WAI/WCAG22/Understanding/label-in-name.html)

### UXA-05 · Task language and the word `colleague`

**Observed.** Directory uses `Search colleagues` as its accessible input label and `Search colleagues by display name (Vietnamese diacritic-insensitive) or username.` as help text. Create community uses `Discoverable by colleagues`. Repository copy also includes `Add colleagues (optional)`, `Add colleague`, and `Select colleague…` in group and Community flows.

**Fix.** Removed all `colleague`/`colleagues` copy across `main.tsx`, `chat.tsx`, and `communities.tsx`, applying the standardized plain-English copy table (`Directory`, `Search directory`, `Search by name or username.`, `Add people (optional)`, `Add person`, `Select a person`, `Add channel`).

**Retest.** Verify product-authored text and accessible labels use the terms in the copy rules below. Check sign-in, Chat, Directory, and Community dialogs. Preserve names and user-authored content. Confirm searches with and without Vietnamese diacritics still work.

### UXA-06 · Start a chat from Chat

**Observed.** **New DM** opens the general Directory screen and leaves its search input unfocused. **Group** opens **Create group chat**. The abbreviated and noun-only labels require users to infer the next step.

**Fix.** Renamed actions to **New chat** and **Create group chat** in `src/Nexus.Client/src/chat.tsx` and auto-focused `#dir-search` when entering Directory from **New chat** in `src/Nexus.Client/src/main.tsx`.

**Retest.** Label the actions **New chat** and **Create group chat**. Start **New chat** at a focused recipient search step, including when reusing Directory. Selecting a person should bring the user to that conversation. Verify both keyboard and pointer paths.

### UXA-07 · Communities sidebar header alignment

**Observed.** At 1440 px, the sidebar and the **Communities** heading both begin at x=68 px. The first 34 px toolbar button begins at that same x-coordinate on a separate row. The adjacent toolbar buttons touch. Chat’s heading begins at x=82 px, 14 px inside the same sidebar edge, and aligns with its actions on one row.

**Fix.** Updated `src/Nexus.Client/src/communities.tsx` to use the shared `.nx-side-header`, `.nx-side-header-row`, `.nx-inline-actions`, and `.nx-conv-scroll` primitives matching Chat's inset and alignment.

**Retest.** Use a shared, token-based sidebar header with a consistent inset and control gap. Align title and actions on one row when space permits. Check long names and the compact layout for collisions.

### UXA-08 · Repeated Community actions

**Observed.** The desktop empty screen presents Create and Discover in the sidebar toolbar, the sidebar empty state, and the main empty-state card. Discovery appears as `Discover`, `Discover public`, and `Discover public communities`. Switching to Discover with zero results leaves the welcome card and the redundant action visible in the main panel.

**Fix.** Consolidated persistent Community controls in the sidebar header with consistent **Browse communities** / **My communities** and **Create community** labels, removed duplicate buttons from the sidebar empty state, and added dedicated Browse states in `src/Nexus.Client/src/communities.tsx`.

**Retest.** Present one prominent next action in each state and one predictable location for persistent actions. Use **Browse communities** as the task label and **My communities** as the return state. Check an empty membership list, a populated membership list, active discovery with zero results, and discovered results at desktop and narrow widths.

### UXA-09 · Directory control heights

**Observed.** At 1440 px, Search measures 31 px tall while the department select and **Search** button each measure 40 px. Their centers align, but their top and bottom edges visibly differ. At 320 px, search stacks above the filter and button without horizontal overflow.

**Fix.** Standardized Directory toolbar input, department select, and Search button heights to shared `--control-h` (`40px`) in `src/Nexus.Client/src/ui/tokens.css` and `src/Nexus.Client/src/style.css`.

**Retest.** Give the input, select, and button one shared toolbar control size. Verify aligned edges in the desktop row and consistent spacing when the toolbar wraps.

### UXA-10 · Trial notice visibility and dismissal

**Observed.** At 1440 px, the notice container is 440 px wide with 592 px of scrollable content and hidden overflow. The dismiss button starts at x=758 px, beyond the container’s right edge at x=627 px. At 390 px, the notice is hidden.

**Fix.** Refactored `.nx-trial-pill` into an expandable notice trigger (truncating only the inner text span) paired with a non-shrinking visible dismiss button and a full-width expandable `#trial-notice-banner` accessible across desktop and narrow viewports in `src/Nexus.Client/src/main.tsx` and `src/Nexus.Client/src/style.css`.

**Retest.** Provide a readable environment indicator across widths. If it expands to show the policy text, make that trigger discoverable and keyboard accessible. If the notice is dismissible, keep the dismiss control within the visible and focusable area. Check both layouts with long text.

### UXA-11 · Conversation header on narrow screens

**Observed.** At 320 px, **Files**, **Report**, and **Block** take 178 px in button widths plus gaps. All three are 28 px tall. The back control and the conversation identity share the space that remains.

**Fix.** Moved **Report user** and **Block user** into a keyboard-accessible **More actions** menu (`aria-haspopup="menu"`, `ArrowUp`/`ArrowDown`/`Escape` focus return) alongside **Files** and added graceful header name truncation in `src/Nexus.Client/src/chat.tsx` and `src/Nexus.Client/src/style.css`.

**Retest.** Keep the conversation identity, back action, and frequent actions readable. Place **Report user** and **Block user** in a labeled **More actions** menu. Preserve keyboard access and verify the header with long display names at 320 and 390 px.

### UXA-12 · Community visibility terminology

**Observed.** Create community defaults to `Public — Discoverable by colleagues` and offers `Private — Invite only`. In an internal app, `Public` needs an explicit organization boundary. The current copy also leaves the distinction between discovery and joining unclear.

**Fix.** The single-window P0-D14 build uses contrasting **`Shared Community`** and **`Private Community`** radio options with helper text for discovery, joining, and Official Community eligibility. The Community header shows the workspace name, visibility (`Shared Community` / `Private Community`), and the member's role. **Add channel** is available to a Community Owner or Admin.

**Retest.** Ready for a browser review with populated Communities. Compare the visibility choices as a member and as an account in another Organization. Verify **Your role** and **Add channel** as Owner, Admin and Member, and verify the Official Community eligibility rule. [Community and Channel decision](../decisions/2026-11-23-p0-d04-community-channel-hierarchy.md)

## Proposed enterprise UI rules

These are proposed additions to the [Nexus Design System & UX Conventions](README.md). The rules should be adopted as shared components, copy conventions, and release checks after review.

| Rule | Acceptance check |
| --- | --- |
| Plain English for product copy | Use sentence case and task labels that describe what happens. Avoid `colleague` and `colleagues` in Nexus-authored copy, including accessible names. Use established, role-specific nouns such as **people**, **members**, **person**, and **organization**. Spell out unfamiliar acronyms in task surfaces. Use **Direct messages** as a section and **New chat** as an action. |
| One term for each destination and object | Use the same destination term in navigation, the page title, help text, and accessible names. Use **Community** for the membership and feed boundary and **Channel** for an optional child. Use **Add channel** in the UI. Selecting a Community opens its feed directly as defined by P0-D04. |
| Shared alignment and density | Use shared primitives and existing spacing tokens for sidebar gutters, heading baselines, control gaps, and header heights. Standardize same-row input, select, and button sizes. Review at 1440 px and across the existing 860 px breakpoint. |
| Clear action hierarchy | Designate one primary action per task surface. Use a verb with a specific object when the action could be ambiguous. Place low-frequency and destructive actions in a labeled contextual menu, and make destructive confirmation identify its target and effect. |
| Complete state feedback | Define loading, empty, error, success, and permission-limited states for each data surface. Show empty only after a successful empty result. Preserve the layout and focus across asynchronous state changes, and provide an actionable error or retry when appropriate. |
| Keyboard and accessible labels | Use visible focus, a programmatic current selection, meaningful button names, and the relevant WAI-ARIA keyboard pattern. Restore dialog focus to the trigger or the next logical control. Ensure the visible label is contained in the accessible name. Check with keyboard and a screen reader. |
| Responsive and touch behavior | Verify reflow at 320 and 390 CSS px, at 200% browser zoom, and around the layout breakpoint. Apply the WCAG 2.2 AA target-size rule, including its exceptions, and target 44 × 44 CSS px for primary touch controls as a Nexus design goal. Check long names, fixed bars, scroll containers, dialog actions, and touch and keyboard operability. |
| Scope and permissions surfaced in context | State who can discover, join, see, and post as applicable. Explain role restrictions where they affect the current task. Use the existing access model as the source of truth for all visibility and control states. |
| Release verification | Run the existing UI compliance script and review key screens in both themes. Check empty and populated data, long names, keyboard focus, mobile layout, role restrictions, and asynchronous states. Record the exact deployment or bundle and review result against each audit ID. |

### Copy decisions to apply during implementation

| Current copy | Proposed Nexus copy |
| --- | --- |
| `New DM` | `New chat` |
| `Group` | `Create group chat` |
| `Teams` on the rail | `Communities` |
| `People` on the rail / `People Directory` | `Directory` |
| `Search colleagues` | `Search directory` |
| `Search colleagues by display name (Vietnamese diacritic-insensitive) or username.` | `Search by name or username.` |
| `Add colleagues (optional)` | `Add people (optional)` |
| `Select colleague…` | `Select a person` |
| `Discover public` | `Browse communities` |
| `Public — Discoverable by colleagues` / `Private — Invite only` | `Shared Community` / `Private Community` with policy-specific helper text |
| `Add sub-channel` | `Add channel` |

References for proposed rules include [Microsoft sentence case](https://learn.microsoft.com/en-us/style-guide/text-formatting/using-type/use-sentence-style-capitalization), [Microsoft acronym guidance](https://learn.microsoft.com/en-us/style-guide/checklists/acronyms-checklist), [WAI dialog pattern](https://www.w3.org/WAI/ARIA/apg/patterns/dialog-modal/), [WAI tabs pattern](https://www.w3.org/WAI/ARIA/apg/patterns/tabs/), [WCAG target size](https://www.w3.org/WAI/WCAG22/Understanding/target-size-minimum), and [WCAG reflow](https://www.w3.org/WAI/WCAG22/Understanding/reflow.html).

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
| Communities | Landing, empty membership list, empty Discover, and the creation dialog were reviewed. The live test account needs access to a populated Community to verify the feed, Channels, posts, announcements, polls, and role-specific states. Dark theme and screen reader checks are pending. |
