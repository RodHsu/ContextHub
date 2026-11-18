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
- Body text: a 1rem system-font baseline; secondary and table text generally 0.875rem
- Page title: 1.5-2.25rem, with a clear hierarchy over section headings
- Letter spacing: `0`
- Radius: 8px or less for containers
- Shadows: menus, drawers, and dialogs only

## App Shell And Responsive Behavior

| Viewport | Navigation | Content composition |
| --- | --- | --- |
| `>= 1200` | 15.5rem labeled sidebar, collapsible task groups | Left-aligned content; wide master-detail layouts |
| `1025-1199` | 224px labeled sidebar | Wrapped tools and single-column overview |
| `<= 1024` | App bar button and native modal navigation drawer | Full-width workspace; local data overflow |


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

## Operations desk composition

The overview uses the Structured Operations Desk composition within Quiet Signal. The shared app bar provides location and global controls; the content has one main page title. PageViewport headers omit the repeated breadcrumb, while standalone PageHeader consumers may retain it for contextual flows.

A compact summary combines service attention, the available snapshot metrics and freshness. The main workspace gives recent errors the larger column; a narrower surface groups work entries, review links and background jobs. Actual log levels and timestamps remain the source of truth. The interface must not infer an unresolved workflow state or a new severity taxonomy from presentation alone.

Healthy and empty states use short text rather than nested framed cards. Workspaces share an alignment edge with the app bar and use available width; prose and technical readers keep their own readable line lengths and overflow frames. At narrow widths the overview stacks in DOM order, and controls grow naturally with text size.

All existing navigation, service warnings, review destinations, refresh warnings, loading/error states and background-work links remain available. Light, dark and pure-black appearances use the same type scale and composition. Visual acceptance compares rendered desktop and mobile views against the approved composition in addition to functional and geometry tests.

Small action and work-entry icons use the supplied [Feather 4.29.2](https://github.com/feathericons/feather/tree/v4.29.2) SVG assets. They are served locally with their [MIT license](../../src/Memory.Dashboard/wwwroot/icons/feather/LICENSE); no runtime icon CDN or font download is required. The existing product logo and navigation icon system remain in use.
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

## Workspace navigation and interaction

The shared shell uses one role-filtered navigation model for the desktop sidebar, mobile drawer and function search. Existing routes remain stable. Monitoring, runtime and background jobs are first-class entries under Operations; instance settings live with account utilities. Governance suggestions at /inbox are distinct from business work items at /project-work-items.

- Sidebar groups are collapsible buttons with expanded state. The current group opens after navigation. Every route uses a recognizable SVG icon and a text label; compact widths use a drawer rather than an alphabet-only rail.
- Function search is a native modal dialog, opened by the search button or Ctrl/Cmd+K. Search matches group, label, purpose and route; empty results provide recovery text. Tab/Enter follow normal link semantics. The query uses a text input with searchbox semantics so a nonempty search does not consume Escape to clear the field. Escape and close buttons use the native dialog focus lifecycle, closing on the first press and returning focus to the trigger.
- The mobile navigation renders the same component and role-filtered model in its own native dialog. Choosing a route closes it; resizing to desktop also closes it. Closed mobile navigation is outside the tab order.
- Dark, light, pure black and system preferences persist across pages and reloads. Pure black uses a black canvas with separate neutral surfaces; system resolves to light/dark.
- A visible-on-focus skip link transfers keyboard focus to the main scroll container and returns it to the top. Its native fragment href remains available before initialization; interactive handling prevents enhanced navigation from leaving focus on the link. Reduced motion and forced colors retain usable navigation.
- The overview uses natural document flow inside the main scroll region: shortcuts, a metric strip, health/risk/review, background work and release identity. Panels must not overlap or compress their contents to fit a viewport.
- The overview's outer page must grow with its contents. It must not shrink while children overflow its box; the main content owns scrolling, with keyboard and wheel access through the release footer.
- Memory search has visible labels. Each result title is a native button so Tab, Enter and Space can select and read an item without a pointer. Advanced conditions start collapsed without changing their query values.

Member navigation reflects the current server route policy: memories, graph, preferences and personal tokens. Hiding a link never substitutes for server authorization. Other domain surfaces retain their existing access requirements.

Implementation guidance: [Modern Web Guidance](https://github.com/GoogleChrome/modern-web-guidance-src), navigation drawer, navbar, dialog, CSS layout, responsive table and accessibility guides. Native showModal/close avoids command-invoker, popover and anchor-positioning compatibility dependencies.

## Published Blazor assets

The Dashboard explicitly sets `RequiresAspNetWebAssets=true` so framework asset inclusion does not depend on Razor discovery during project-only restore. The Docker restore layer also preserves the existing `App.razor` copy. The publish step rejects an image without `wwwroot/_framework/blazor.web.js`. Container validation must verify script delivery and an active Blazor circuit, in addition to an HTTP 200 page. See [Microsoft Blazor project structure](https://learn.microsoft.com/en-us/aspnet/core/blazor/project-structure?view=aspnetcore-10.0) and [the upstream Docker restore issue](https://github.com/dotnet/aspnetcore/issues/69341).

## Preference workspace

Preferences paginate the already loaded records in groups of ten. Counts and page totals describe that loaded, category-filtered set; the interface identifies the current 50-record load limit. Paging preserves the active editor. Category changes return to page one, while refresh and save locate the selected record in a valid page. Desktop retains the bounded list and editor columns; at 1360px and below the panels flow naturally and the main content owns vertical scrolling. This avoids trapping the pointer between a bounded list and its page.

Editing a preference preserves its loaded importance and confidence scores. New preferences use the existing 0.95 defaults for both scores.

## Inventory filters

Files and Secrets bind their project, search and refresh warning text to the current page state. Selecting a project and refreshing preserves the selection; searching and refreshing preserves the filter. A successful refresh must not display a literal field name as a warning.

The scope selector includes the actual queried project even when it is absent from the project catalog, preserving its exact value while deduplicating other options without regard to case. This keeps a configured default visible without silently switching the query or its authorization scope.
