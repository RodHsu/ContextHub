# ContextHub Quiet Signal UI Baseline

> Public UI, UX, CIS, and responsive design contract for the ContextHub dashboard.

## Design Source

`Quiet Signal` is the active design baseline for the dashboard. Public documentation keeps the reusable product contract and local mockups. Private design-tool session IDs and one-off implementation notes belong outside the tracked public repo.

## CIS

`Quiet Signal` should feel quiet, precise, trustworthy, and fast. The interface should avoid neon cyberpunk styling, terminal-like decorative frames, marketing hero sections, and card-heavy visual noise.

- Canvas: deep neutral background
- Surface: restrained graphite-like panels
- Primary: teal, reserved for primary actions, selected state, and focus
- Text: high-contrast neutral foreground
- Status: success, warning, danger, and info colors paired with icon or text
- Typography: UI text in a readable sans-serif; IDs, JSON, logs, timestamps, code, and tokens in monospace
- Body text: generally 14-16px
- Page title: generally 24px
- Letter spacing: `0`
- Radius: 8px or less for containers
- Shadows: menus, drawers, and dialogs only

## App Shell And Responsive Behavior

| Viewport | Navigation | Content composition |
| --- | --- | --- |
| `>= 1440` | Expanded sidebar | 12-column grid; master-detail can use 7/5 split |
| `1025-1365` | Icon rail or compact sidebar | Secondary columns collapse into bounded drawers |
| `768-1024` | Rail or overlay drawer | Charts, queues, lists, and details become single-column or route transitions |
| `< 768` | Top app bar and overlay navigation | List-to-detail workflows; no compressed desktop tables |

Hard rules:

- Document and page body must not horizontally scroll.
- Tables, JSON, code, diffs, payloads, and logs may horizontally scroll only inside their own named frames.
- A component should own at most one scroll axis.
- Dedicated readers must provide visible scrollbars, stable gutter, selectable text, copy actions, and wrap controls where useful.
- Tablet and mobile action areas must not cover content without safe-area spacing.
- Sidebar must compress predictably at narrower widths.

## Shared Component Contract

- `AppShell`: sidebar, rail, and drawer states share one navigation model.
- `PageHeader`: title, context, local update time, primary action; no hero layout.
- `MetricStrip`: grouped metrics inside one surface, not standalone KPI card walls.
- `DataGrid`: sticky header, stable row height, column priority, local horizontal overflow, row action menu.
- `MasterDetail`: desktop split view; tablet and mobile use drawer or full-width detail route.
- `ContentReader`: selectable, copyable, wrappable, bounded, and scrollable within its frame.
- `FormSection`: label above control, inline validation, dirty state, save guard, disabled explanation.
- `RefreshState`: keep old data visible while refresh runs; update client-local timestamp on success.
- `DestructiveDialog`: object name, impact preview, consequence, and typed confirmation for high-risk actions.
- `AsyncState`: default, loading, stale, empty, error, retry, success, focus, and disabled states preserve layout.

## Route Matrix

| Route | Area | Archetype |
| --- | --- | --- |
| `/` | Operations | metric strip + health trend + incident queue + service table |
| `/monitoring` | Operations | dominant chart + annotated events + service table |
| `/runtime` | Operations | effective/source config + diff + rollback |
| `/logs` | Operations | dedicated selectable log reader |
| `/jobs` | Operations | queue table + bounded detail drawer |
| `/performance` | Operations | KPI strip + percentile/throughput + endpoint table |
| `/graph` | Knowledge | unframed graph workspace + node drawer |
| `/memories` | Knowledge | list + content/revision/chunk readers |
| `/sources` | Knowledge | connector list + sync history + payload reader |
| `/retention` | Knowledge | candidate list + guarded decision detail |
| `/inbox` | Governance | prioritized proposal queue + diff review |
| `/chatgpt-proposals` | Governance | AI proposal queue + before/after + JSON |
| `/governance` | Governance | policy list + enforcement + publish guard |
| `/evaluation` | Governance | evaluation runs + evidence reader |
| `/settings` | Administration | grouped form + dirty save bar |
| `/security` | Administration | policy/session controls + danger zone |
| `/storage` | Administration | capacity + namespace table + cleanup impact |
| `/preferences` | Administration | personal settings + local-time preview |
| `/account/tokens` | Account | token lifecycle + one-time secret reveal |
| `/login` | Boundary | focused authentication + validation/recovery |
| `/forbidden` | Boundary | permission explanation + recovery action |
| `/not-found` | Boundary | route recovery + search/home action |
| `/Error` | Boundary | retry + selectable diagnostic ID |

## Key Mockups

Curated public mockups are stored in [`mockups/quiet-signal`](mockups/quiet-signal).

- [`overview-desktop.png`](mockups/quiet-signal/overview-desktop.png)
- [`monitoring-desktop.png`](mockups/quiet-signal/monitoring-desktop.png)
- [`performance-desktop.png`](mockups/quiet-signal/performance-desktop.png)
- [`memories-desktop.png`](mockups/quiet-signal/memories-desktop.png)
- [`memories-tablet-list.png`](mockups/quiet-signal/memories-tablet-list.png)
- [`memories-tablet-detail.png`](mockups/quiet-signal/memories-tablet-detail.png)
- [`chatgpt-proposals-desktop.png`](mockups/quiet-signal/chatgpt-proposals-desktop.png)
- [`governance-desktop.png`](mockups/quiet-signal/governance-desktop.png)
- [`settings-desktop.png`](mockups/quiet-signal/settings-desktop.png)
- [`boundary-states-desktop.png`](mockups/quiet-signal/boundary-states-desktop.png)
- [`login-mobile.png`](mockups/quiet-signal/login-mobile.png)

## Implementation Gate

Each production route should pass:

1. Screenshot QA for desktop, compact desktop, tablet, mobile, and constrained app-browser widths.
2. No body or document horizontal overflow.
3. Sidebar, rail, and drawer switch at the intended breakpoints.
4. Tables, readers, drawers, and sticky actions do not overlap or escape their frames.
5. Light/dark, loading, empty, error, stale, disabled, focus, and destructive confirmation states remain usable.
6. Local time, selectable technical values, copy feedback, keyboard focus, and reduced-motion behavior are covered.
