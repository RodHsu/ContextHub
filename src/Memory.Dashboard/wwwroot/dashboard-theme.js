(() => {
    const storageKey = "contextHub.dashboard.theme";
    const lightMediaQuery = window.matchMedia("(prefers-color-scheme: light)");
    let initialized = false;

    function normalizePreference(value) {
        return value === "light" || value === "dark" || value === "system"
            ? value
            : "dark";
    }

    function resolveTheme(preference) {
        if (preference === "system") {
            return lightMediaQuery.matches ? "light" : "dark";
        }

        return preference;
    }

    function applyTheme(preference) {
        const normalized = normalizePreference(preference);
        const resolved = resolveTheme(normalized);
        document.documentElement.dataset.themePreference = normalized;
        document.documentElement.dataset.theme = resolved;
        document.documentElement.style.colorScheme = resolved;
        return normalized;
    }

    function currentPreference() {
        return normalizePreference(window.localStorage.getItem(storageKey) || document.documentElement.dataset.themePreference || "dark");
    }

    function handleSystemThemeChange() {
        if (currentPreference() === "system") {
            applyTheme("system");
        }
    }

    window.contextHubTheme = {
        initialize() {
            const preference = applyTheme(currentPreference());
            if (!initialized) {
                lightMediaQuery.addEventListener("change", handleSystemThemeChange);
                initialized = true;
            }

            return preference;
        },
        getPreference() {
            return currentPreference();
        },
        setPreference(preference) {
            const normalized = applyTheme(preference);
            window.localStorage.setItem(storageKey, normalized);
            return normalized;
        }
    };
})();
