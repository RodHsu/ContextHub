const root = document.documentElement;

let frameHandle = 0;
let started = false;

function clamp(value, min, max) {
    return Math.max(min, Math.min(value, max));
}

function setMetric(name, value) {
    root.style.setProperty(name, `${Math.round(value)}px`);
}

function updateViewportMetrics() {
    const viewportHeight = Math.max(window.innerHeight || 0, 480);
    const viewportWidth = Math.max(window.innerWidth || 0, 320);

    const isWide = viewportWidth >= 1600;
    const isDesktop = viewportWidth >= 1200;
    const isTablet = viewportWidth >= 960;

    const panelScrollMaxHeight = clamp(
        viewportHeight - (isWide ? 250 : isDesktop ? 272 : isTablet ? 304 : 336),
        280,
        isWide ? 900 : 780);

    const tallPanelScrollMaxHeight = clamp(
        viewportHeight - (isWide ? 206 : isDesktop ? 224 : isTablet ? 252 : 284),
        340,
        isWide ? 980 : 860);

    const codeBlockMaxHeight = clamp(
        viewportHeight - (isWide ? 428 : isDesktop ? 452 : isTablet ? 492 : 536),
        200,
        560);

    const storagePanelMaxHeight = clamp(
        viewportHeight - (isWide ? 114 : isDesktop ? 122 : isTablet ? 150 : 184),
        280,
        viewportHeight - 32);

    setMetric("--dashboard-viewport-height", viewportHeight);
    setMetric("--dashboard-viewport-width", viewportWidth);
    setMetric("--panel-scroll-max-height", panelScrollMaxHeight);
    setMetric("--panel-scroll-max-height-tall", tallPanelScrollMaxHeight);
    setMetric("--code-block-max-height", codeBlockMaxHeight);
    setMetric("--storage-panel-max-height", storagePanelMaxHeight);
}

function scheduleViewportMetricsUpdate() {
    if (frameHandle !== 0) {
        cancelAnimationFrame(frameHandle);
    }

    frameHandle = requestAnimationFrame(() => {
        frameHandle = 0;
        updateViewportMetrics();
    });
}

function startViewportMetrics() {
    if (started) {
        return;
    }

    started = true;
    scheduleViewportMetricsUpdate();

    window.addEventListener("resize", scheduleViewportMetricsUpdate, { passive: true });
    window.addEventListener("orientationchange", scheduleViewportMetricsUpdate, { passive: true });
    document.addEventListener("visibilitychange", scheduleViewportMetricsUpdate);
}

startViewportMetrics();
