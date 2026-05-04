window.contextHubTime = {
    formatLocalTimestamp(isoValue) {
        if (!isoValue) {
            return "";
        }

        const value = new Date(isoValue);
        if (Number.isNaN(value.getTime())) {
            return isoValue;
        }

        const year = value.getFullYear();
        const month = String(value.getMonth() + 1).padStart(2, "0");
        const day = String(value.getDate()).padStart(2, "0");
        const hour = String(value.getHours()).padStart(2, "0");
        const minute = String(value.getMinutes()).padStart(2, "0");
        const second = String(value.getSeconds()).padStart(2, "0");
        const offsetMinutes = -value.getTimezoneOffset();
        const sign = offsetMinutes >= 0 ? "+" : "-";
        const absoluteOffsetMinutes = Math.abs(offsetMinutes);
        const offsetHours = String(Math.floor(absoluteOffsetMinutes / 60)).padStart(2, "0");
        const offsetRemainderMinutes = String(absoluteOffsetMinutes % 60).padStart(2, "0");

        return `${year}-${month}-${day} ${hour}:${minute}:${second} GMT${sign}${offsetHours}:${offsetRemainderMinutes}`;
    },

    formatRelativeTimestamp(isoValue) {
        if (!isoValue) {
            return "";
        }

        const value = new Date(isoValue);
        if (Number.isNaN(value.getTime())) {
            return isoValue;
        }

        const deltaSeconds = Math.max(0, Math.round((Date.now() - value.getTime()) / 1000));
        if (deltaSeconds < 60) {
            return `${deltaSeconds} 秒前`;
        }

        const deltaMinutes = Math.floor(deltaSeconds / 60);
        if (deltaMinutes < 60) {
            return `${deltaMinutes} 分鐘前`;
        }

        const deltaHours = Math.floor(deltaMinutes / 60);
        if (deltaHours < 24) {
            return `${deltaHours} 小時前`;
        }

        const deltaDays = Math.floor(deltaHours / 24);
        return `${deltaDays} 天前`;
    }
};

(() => {
    const selector = "time.client-local-time[data-local-iso]";

    function applyLocalTimestamp(element) {
        if (!(element instanceof HTMLElement)) {
            return;
        }

        const isoValue = element.dataset.localIso;
        if (!isoValue) {
            return;
        }

        const formatted = window.contextHubTime.formatLocalTimestamp(isoValue);
        if (formatted) {
            element.textContent = formatted;
        }
    }

    function refreshLocalTimestamps(root) {
        if (!root) {
            return;
        }

        if (root.matches?.(selector)) {
            applyLocalTimestamp(root);
        }

        root.querySelectorAll?.(selector).forEach(applyLocalTimestamp);
    }

    function initialize() {
        refreshLocalTimestamps(document);

        if (typeof MutationObserver === "undefined") {
            return;
        }

        const observer = new MutationObserver(mutations => {
            mutations.forEach(mutation => {
                mutation.addedNodes.forEach(node => refreshLocalTimestamps(node));

                if (mutation.type === "attributes" && mutation.target instanceof HTMLElement) {
                    applyLocalTimestamp(mutation.target);
                }
            });
        });

        observer.observe(document.body, {
            childList: true,
            subtree: true,
            attributes: true,
            attributeFilter: ["data-local-iso"]
        });
    }

    if (document.readyState === "loading") {
        document.addEventListener("DOMContentLoaded", initialize, { once: true });
    }
    else {
        initialize();
    }
})();
