(() => {
    const refreshPath = "/account/session/refresh";
    const activeWindowMilliseconds = 5 * 60 * 1000;
    const minimumRequestIntervalMilliseconds = 5 * 60 * 1000;
    let lastActivityAt = 0;
    let lastRefreshAt = 0;
    let refreshInFlight = false;

    const refreshWhenActive = () => {
        const now = Date.now();
        if (document.visibilityState !== "visible" ||
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
            .then(response => {
                if (response.ok) {
                    lastRefreshAt = Date.now();
                }
            })
            .catch(() => {
                // The next user action retries; preserve the current session on transient failures.
            })
            .finally(() => {
                refreshInFlight = false;
            });
    };

    const recordActivity = () => {
        lastActivityAt = Date.now();
        refreshWhenActive();
    };

    for (const eventName of ["pointerdown", "keydown", "touchstart"]) {
        window.addEventListener(eventName, recordActivity, { passive: true });
    }

    window.setInterval(refreshWhenActive, 60 * 1000);
})();
