# ContextHub Product Design + Stitch UI/UX Design Language

> 2026-07-08 Product Design baseline. Stitch project: `projects/4533056430393435785`.
>
> Active design system: `assets/33c0c23e5fc4475e8852b7906cfe6bc6` (`ContextHub Operations`).
>
> Legacy design drafts from earlier full-site / scroll-shell passes are retired. Use only the Product Design + Stitch screens and SVG boards listed in this document for new UI/UX decisions.

## 1. Design Intent

ContextHub is a modern SaaS operations console for engineers and administrators. It must feel current, precise, dense, and calm; it must not look like a legacy enterprise admin page, an old NOC wall, or a decorative landing page.

Primary user jobs:

1. Know whether ContextHub is healthy and trustworthy right now.
2. Diagnose logs, jobs, runtime, graph, memory, and source issues without losing context.
3. Review AI-suggested writes with evidence, policy, audit trail, and explicit control.
4. Manage settings, security, storage, and tokens without exposing secrets or enabling accidental destructive action.

## 2. Reference Translation

Product Design references are used as patterns, not branding:

- shadcn-admin: command search, grouped sidebar, keyboard-first app shell.
- Tabler: clean responsive dashboard hierarchy and reusable component rhythm.
- TailAdmin / Flowbite Admin Dashboard: practical CRUD, forms, tables, drawers, modals, auth and boundary states.
- Grafana / OpenObserve-style observability: metrics, logs, alerts, query/result workflow, and incident scanning.

ContextHub translation:

- Use a command palette for route jump, memory lookup, job lookup, log query, and proposal search.
- Use grouped navigation: Operations Monitoring, Knowledge Work, Review and Governance, Admin Settings, Personal Account.
- Treat observability pages as one workbench family: metrics, query, results, local scroll, detail drawer.
- Treat knowledge pages as one workbench family: graph/table/preview/detail with provenance and conflict handling.
- Treat `/chatgpt-proposals` as a review workbench, not a plain table.
- Keep destructive and durable actions gated, explain disabled states, and record audit context.

## 3. Visual System

Core tokens:

| Role | Token |
| --- | --- |
| Background | `#0C1322` |
| Surface | `#151C28` |
| Surface Alt | `#1B2432` |
| Border | `#2A3545` |
| Text Primary | `#F8FAFC` |
| Text Secondary | `#9AA7B8` |
| Primary | `#0B63CE` |
| Success | `#0E9F6E` |
| Warning | `#B7791F` |
| Danger | `#C81E1E` |

Typography:

- Inter first; system UI and Microsoft JhengHei fallback.
- Body text normally 14px-16px.
- Letter spacing is `0` in implementation unless a specific Stitch label token requires micro-label spacing.
- Technical IDs, JSON, payloads, and copied values use monospace and remain selectable.

Shape and density:

- Major panels: max 8px radius.
- Controls: 6px to 8px radius.
- Use tonal layers and 1px hairlines before shadows.
- Prefer refined dot chips over large filled badges.
- Avoid nested cards, decorative gradients, blobs, oversized empty cards, and full-width tablet menus.

## 4. Layout Contract

```text
App viewport
├── Sidebar
│   ├── desktop: 240px expanded
│   ├── 641px-1024px: 64px icon rail
│   └── under 640px: overlay drawer
├── Topbar: 56px
└── Content scroll host
    ├── page header
    ├── route workbench
    ├── table/code/diff local horizontal scroll
    └── detail drawer or stacked tablet panel
```

Hard rules:

- `body` never owns horizontal scroll.
- Content area owns normal vertical scrolling.
- Tables, code blocks, JSON, payload diffs, and log cells own local horizontal scroll.
- Sidebar must not create a second visible vertical scrollbar in normal use.
- Panels, detail drawers, menus, tooltips, and popovers must not overlap or clip data frames.
- Tab S7 portrait and landscape must remain readable with icon rail and stacked detail panels.

## 5. State, Safety, Refresh, Loading

State vocabulary:

- `pending`, `validating`, `running`, `syncing`, `paused`, `stale`, `blocked`, `failed`, `rejected`, `approved`, `applied`, `completed`.

Safety:

- Pending and validating items must never look applied or safe to consume.
- Disabled actions must expose a reason.
- Destructive actions require confirmation; irreversible actions require typed confirmation.
- Secret values use one-time reveal; token secrets are never displayed again.
- Client UI renders timestamps in client-local time unless UTC is explicitly required.
- Non-selectable by default: chrome, labels, buttons, nav.
- Selectable by default: IDs, token prefixes, JSON, code, diffs, log lines, copied payloads.

Refresh and loading:

- App-level route load uses a top progress strip.
- Panels use skeletons, not blocking full-screen spinners.
- Tables use header-preserving row skeletons.
- Code/diff blocks use line skeletons.
- Live refresh uses subtle pulse; paused refresh uses neutral pause chip; stale data uses warning banner and blocks misleading actions.
- Retry flows keep request/correlation id visible.

Motion:

- Hover/focus: 120ms.
- Menu/popover: 180ms.
- Drawer/panel transition: 220ms.
- Route transition: 240ms.
- Live-tail new-row highlight: 700ms fade.
- Reduced-motion fallback uses fade or snap instead of slide, pan, zoom, or pulse.

## 6. Active Stitch Screens And SVG Boards

| Scope | Routes / Function | Stitch screen | Repo SVG |
| --- | --- | --- | --- |
| Product design strategy | Cross-route UX strategy, roles, references | `projects/4533056430393435785/screens/27112231d8494e448ad6e7fdf42eed86` | `docs/design/svg/00-product-design-strategy.svg` |
| Modern visual direction | Visual direction and product surface | `projects/4533056430393435785/screens/d14425b3d81c4f299ed15f5b901628a7` | `docs/design/svg/00-modern-visual-direction.svg` |
| UI system, states, motion | Components, loading, refresh, safety controls | `projects/4533056430393435785/screens/d6284134138544659d04b026954d010f` | `docs/design/svg/01-ui-system-states-motion.svg` |
| App shell and navigation | Sidebar, topbar, command palette, account chrome | `projects/4533056430393435785/screens/116b0b12c50d4a31b8aa4025c10f7d9a` | `docs/design/svg/02-app-shell-navigation-command.svg` |
| Auth and boundary | `/login`, `/forbidden`, `/not-found`, `/Error` | `projects/4533056430393435785/screens/11e2b475d8b541db953b6fd6b2b710ce` | `docs/design/svg/03-auth-boundary-pages.svg` |
| Overview and monitoring | `/`, `/monitoring` | `projects/4533056430393435785/screens/454b419a89264cc29d07e005029bda83` | `docs/design/svg/04-operations-overview-monitoring.svg` |
| Runtime workbench | `/runtime`, `/logs`, `/jobs`, `/performance` | `projects/4533056430393435785/screens/80b6642ae1b249b3ab921d3ab67129e8` | `docs/design/svg/05-runtime-logs-jobs-performance.svg` |
| Knowledge workspace | `/graph`, `/memories` | `projects/4533056430393435785/screens/baa8629daf8741ef8469d0939d3fc0d8` | `docs/design/svg/06-knowledge-graph-memory.svg` |
| Source lifecycle | `/sources`, `/inbox`, `/retention` | `projects/4533056430393435785/screens/6d5ffa8ae48748ff8832e8a3f8d312fb` | `docs/design/svg/07-sources-inbox-retention.svg` |
| Review and governance | `/chatgpt-proposals`, `/governance`, `/evaluation` | `projects/4533056430393435785/screens/8b98c7ea4f0645b2b8da732e7c6fa89a` | `docs/design/svg/08-review-governance-evaluation.svg` |
| Admin and account | `/preferences`, `/storage`, `/security`, `/settings`, `/account/tokens` | `projects/4533056430393435785/screens/992f81554dc64034807a9753b0523024` | `docs/design/svg/09-admin-settings-account-tokens.svg` |
| RWD validation | Viewports, loading, refresh, motion QA | `projects/4533056430393435785/screens/17e2fea5a4b347b9bbd3a8e485b73fe1` | `docs/design/svg/10-rwd-loading-refresh-motion.svg` |
| Feature design atlas | All routes mapped to page/function/state/guardrail/loading/refresh/motion/RWD | `projects/4533056430393435785/screens/450468dcfc354e9b9e706085e4cf86b2` | `docs/design/svg/11-feature-design-atlas.svg` |
| Operations feature atlas | `/`, `/monitoring`, `/runtime`, `/logs`, `/jobs`, `/performance` | `projects/4533056430393435785/screens/8950c9e0f9a14b93ac4bf5b7283e6f90` | `docs/design/svg/12-operations-feature-atlas.svg` |
| Knowledge feature atlas | `/graph`, `/memories`, `/retention`, `/sources` | `projects/4533056430393435785/screens/79ba882411d44e13b94cfc6d1de72013` | `docs/design/svg/13-knowledge-feature-atlas.svg` |
| Review and governance feature atlas | `/inbox`, `/governance`, `/evaluation`, `/chatgpt-proposals` | `projects/4533056430393435785/screens/f296315d38f5483b8845d1352d75d3c5` | `docs/design/svg/14-review-governance-feature-atlas.svg` |
| Admin, account and boundary feature atlas | `/preferences`, `/storage`, `/security`, `/settings`, `/account/tokens`, `/login`, `/forbidden`, `/not-found`, `/Error` | `projects/4533056430393435785/screens/b80a7c7dbf8f4c2f91151dde97db5a38` | `docs/design/svg/15-admin-boundary-feature-atlas.svg` |

Note: current Stitch MCP exposes screen generation and retrieval, but not native SVG export. The SVG files are repo-saved summary boards derived from Product Design + Stitch screens and design contracts.
