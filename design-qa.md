# Cross-project discussions design QA

## Comparison target

- Source visual truth: `C:/Users/User/.codex/visualizations/2026/07/18/019f75b5-e190-78e3-b2b5-a5e5d4c50eda/cross-project-discussion-stitch-preview.png`
- Intended viewport: desktop, 1440 × 1024, dark theme, selected discussion state.
- Implementation route: `/discussions`.
- Implementation screenshot: unavailable.

## Findings

- [P1] Browser-rendered implementation has not been captured or compared.
  Location: `/discussions` at 1440 × 1024.
  Evidence: the selected Stitch source preview is available, but this environment does not expose an interactive browser capture tool. The existing `DashboardBrowserUiTests` run exceeded 120 seconds without producing a result.
  Impact: layout proportions, scrolling ownership, responsive breakpoints, and interaction affordances cannot be claimed visually verified.
  Fix: run the `/discussions` route in the Dashboard browser harness at desktop, tablet, and mobile widths; capture the selected-thread state; compare it against the source preview and resolve any P0–P2 drift.

## Fidelity surfaces

- Fonts and typography: implementation uses the existing Dashboard Inter/system and JetBrains Mono conventions; browser comparison is pending.
- Spacing and layout rhythm: designed as list / thread / inspector panels with responsive two- and one-column fallbacks; browser comparison is pending.
- Colors and visual tokens: uses existing Dashboard surface, border, accent, and status tokens; browser comparison is pending.
- Image quality and asset fidelity: the selected source contains no required raster illustration or logo asset beyond existing product branding.
- Copy and content: uses Traditional Chinese labels for host project, participant visibility, discussion creation, and replies.

## Implementation checklist

1. Capture `/discussions` with populated browser-test data at 1440 × 1024.
2. Check create, selection, and reply controls plus the list / stream / inspector overflow behavior.
3. Repeat at 1011 px and 390 px widths.
4. Update this report with screenshot paths, comparison findings, fixes, and a final result.

## Follow-up polish

- Consider adding per-participant read timestamps after the core workflow is visually verified.

final result: blocked
