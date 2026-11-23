# Nexus Design System & UX Conventions

- Status: active (enforced by [`scripts/audit/Test-UiDesignCompliance.ps1`](../../scripts/audit/Test-UiDesignCompliance.ps1))
- Last updated: 2026-11-24
- Visual language: **Ocean Blue Slack + Teams Hybrid** (Light + Dark mode), matching the product brand **Nexus**.

## 1. Core UI/UX rules

1. **Product name:** Always display **`Nexus`** to end users across Web SPA, Desktop window titles, tray menu, dialogs, and installer shortcuts (never `"Project Nexus"` in end-user UI).
2. **Language & copy conventions:** All product copy, labels, buttons, empty states, error messages, and accessibility (`aria-*`) labels are in plain **English** using sentence case. Preserve people's names and user-authored content in their original language. Avoid `colleague`/`colleagues` (use `people`, `person`, `members`, `organization`). Keep visible labels aligned with accessible names (WCAG Label in Name) and use consistent destination names (**Chat**, **Communities**, **People**, **Profile & security**, **HR import**) with `aria-current="page"`.
3. **Single source of truth for tokens:** All color, spacing, radius, shadow, and typography tokens live strictly in [`src/Nexus.Client/src/ui/tokens.css`](../../src/Nexus.Client/src/ui/tokens.css). **Zero raw `#hex` literals** are allowed outside `tokens.css`. Use shared `--control-h` (`40px`) for aligned toolbar inputs, selects, and buttons, and shared sidebar/filter-pill primitives (`.nx-side-header`, `.nx-comm-sidebar-head`, `.nx-conv-scroll`, `.nx-filter-pills`) across Chat, Communities, and People.
4. **Immediate action feedback:** Interactive buttons provide tactile `:active` press feedback, and optimistic message sends, community posts, and comments render dimmed (`pending`) with a status label (`Sending…` / `Publishing…`) until server confirmation.
5. **Safe HTML (`SEC-10`):** Never use `dangerouslySetInnerHTML` except when passing the direct output of `renderSafeMarkdown(...)` from [`src/Nexus.Client/src/markdown.ts`](../../src/Nexus.Client/src/markdown.ts).
6. **Single-window multi-workspace Communities ([P0-D04](../decisions/2026-11-23-p0-d04-community-channel-hierarchy.md), [P0-D14](../decisions/2026-11-23-p0-d14-personal-chat-and-organizations.md)):** All joined Workspaces appear simultaneously in the Communities sidebar under collapsible Workspace headers (`▾ Workspace [⚙] [+]`) with zero workspace switcher combobox and no duplicate section headings. Selecting a Community opens its main feed directly (no forced `# General` sub-channel). Optional sub-channels appear beneath the Community with a subtle `+ Add channel` action for Owners/Admins. Unified DMs and group chats live in Chat with `All / Direct / Groups` filter pills.
7. **Standard community creation:** Use a single "Create community" action with contrasting **`Shared Community`** and **`Private Community`** radio options; `Official` is a separate designation granted by a verified `CommsAdmin` (`M5.2`).
8. **Prototypes are visual guides only:** See [`../mockups/README.md`](../mockups/README.md). Backend always enforces authorization.

## 2. Token architecture (`src/Nexus.Client/src/ui/tokens.css`)

| Category | Key tokens | Notes |
|---|---|---|
| **Brand ramp (Ocean Blue)** | `--brand-950` (`#0B1F33`) → `--brand-600` (`#1E70B8`) → `--brand-50` (`#F0F7FD`) | Left App Rail uses `--brand-950`; primary buttons & accents use `--brand-600`/`--brand-700` |
| **Surfaces** | `--bg-app`, `--bg-sidebar`, `--bg-surface`, `--bg-subtle`, `--bg-hover`, `--bg-active`, `--bg-msg-own`, `--bg-msg-other` | Full light and `body.dark` overrides (`localStorage['nexus-theme']`) |
| **Text & borders** | `--text-primary`, `--text-secondary`, `--text-muted`, `--border`, `--border-soft`, `--border-focus`, `--focus-ring` | High contrast across light and dark themes |
| **Semantic & presence** | `--success`, `--warning`, `--danger`, `--dot-online`, `--dot-reconnecting`, `--dot-offline` | Presence dots, inline status messages, inline toasts |
| **Spacing & radii** | `--space-1` (`4px`) → `--space-8` (`32px`), `--radius-sm`/`md`/`lg`/`xl`/`pill` | Consistent density across desktop & mobile |
| **Layout** | `--rail-w: 68px`, `--side-w: 304px`, `--drawer-w: 296px`, `--topbar-h: 48px` | Collapses cleanly under `860px` viewport width |

## 3. Shared UI primitives (`src/Nexus.Client/src/ui/`)

Import from `./ui` rather than duplicating markup or ad-hoc CSS:

| Component | File | Purpose |
|---|---|---|
| `Button` | `ui/Button.tsx` | Variants: `default`, `primary`, `ghost`, `danger`; sizes: `sm`, `md`; `iconOnly` (must carry `aria-label`), `fullWidth` |
| `Badge` | `ui/Badge.tsx` | Standardized role and success pills. Verification state remains enforced by server policy and is not shown as a warning badge in member-facing UI. |
| `Avatar`, `getInitials` | `ui/Avatar.tsx` | User (`circle`) and Group (`rounded-square`) avatars in `sm`/`md`/`lg`, optional online/offline presence dot & cache busting |
| `Modal` | `ui/Modal.tsx` | Accessible focus-trapped dialog powered by headless `@radix-ui/react-dialog` (Escape key, backdrop click, `aria-modal`) |
| `ToastBar` | `ui/Toast.tsx` | Inline `error`/`ok`/`info` status banner with dismiss button and proper ARIA role |
| `Card`, `Field`, `EmptyState` | `ui/Card.tsx` | Consistent surface cards, labeled form fields, and empty-state prompts |
| Icons | `lucide-react` | MIT-licensed tree-shaken SVG icon library (pass `aria-hidden="true"` when decorative) |

## 4. Automated compliance gate

Run before every commit (also wired into [`scripts/Run-Tests.ps1`](../../scripts/Run-Tests.ps1) and CI):

```powershell
pwsh -File .\scripts\audit\Test-UiDesignCompliance.ps1
```

Checks enforced automatically:
- `UI-TOKEN-FILE`: `tokens.css` exists with required Ocean Blue light/dark tokens.
- `UI-NO-HARDCODED-HEX`: Zero raw `#hex` literals outside `ui/tokens.css`.
- `UI-BRAND-NAME`: Zero `"Project Nexus"` strings in end-user Web or Desktop UI.
- `UI-SAFE-HTML`: `dangerouslySetInnerHTML` restricted strictly to `renderSafeMarkdown(...)`.
- `UI-ICON-BUTTON-A11Y`: Every `iconOnly` `<Button>` declares `aria-label`.
- `UI-COPY-NO-COLLEAGUE`: Zero `colleague`/`colleagues` wording in Nexus UI `.tsx` copy (`UXA-05`).

## 5. Manual UX & Accessibility Audits

- Track browser smoke-test findings and retest checklists in [`UX-UI-AUDIT.md`](UX-UI-AUDIT.md).
