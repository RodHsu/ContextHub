(() => {
    const refreshPath = "/account/session/refresh";
    const activeWindowMilliseconds = 5 * 60 * 1000;
    const minimumRequestIntervalMilliseconds = 5 * 60 * 1000;
    let lastActivityAt = 0;
    let lastRefreshAt = 0;
    let refreshInFlight = false;

    const refreshWhenActive = () => {
        const now = Date.now();
        if (!document.querySelector(".dashboard-shell[data-dashboard-interactive='true']") ||
            document.visibilityState !== "visible" ||
            now - lastActivityAt > activeWindowMilliseconds ||
            now - lastRefreshAt < minimumRequestIntervalMilliseconds ||
            refreshInFlight) {
            return;
        }

        refreshInFlight = true;
        fetch(refreshPath, {
            credentials: "same-origin",
            cache: "no-store"
        })
            .then(async response => {
                if (response.status === 204) {
                    // Complete the empty response body before recording a successful refresh.
                    await response.arrayBuffer();
                    lastRefreshAt = Date.now();
                }
            })
            .catch(() => {
                // A later active timer retries; preserve the current session on transient failures.
            })
            .finally(() => {
                refreshInFlight = false;
            });
    };

    const recordActivity = () => {
        lastActivityAt = Date.now();
        // Refresh on the periodic timer; user input may immediately navigate or submit.
    };

    for (const eventName of ["pointerdown", "keydown", "touchstart"]) {
        window.addEventListener(eventName, recordActivity, { passive: true });
    }

    window.setInterval(refreshWhenActive, 60 * 1000);
})();
