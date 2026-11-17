(() => {
    let initialized = false;
    const mobile = window.matchMedia("(max-width: 1024px)");

    function synchronizeTrigger() {
        const trigger = document.querySelector("[data-mobile-nav-open]");
        trigger?.setAttribute("aria-expanded", String(document.getElementById("workspace-mobile-nav")?.open === true));
    }

    function openDialog(id) {
        const dialog = document.getElementById(id);
        if (!dialog || dialog.open) return;
        dialog.showModal();
        synchronizeTrigger();
    }

    window.contextHubShell = {
        initialize() {
            if (initialized) return;
            initialized = true;
            document.addEventListener("click", event => {
                if (!(event.target instanceof Element)) return;
                const skipLink = event.target.closest("a[data-skip-to-content]");
                const main = document.getElementById("dashboard-main-content");
                if (skipLink && main instanceof HTMLElement && event.button === 0 &&
                    !event.ctrlKey && !event.metaKey && !event.shiftKey && !event.altKey) {
                    event.preventDefault();
                    main.focus({ preventScroll: true });
                    main.scrollTop = 0;
                    return;
                }
                if (event.target.closest("[data-command-open]")) openDialog("workspace-command");
                if (event.target.closest("[data-mobile-nav-open]")) openDialog("workspace-mobile-nav");
                const dialog = event.target.closest("dialog");
                if (dialog && (event.target.closest("[data-dialog-close]") || event.target.closest("a[href]"))) {
                    dialog.close();
                    synchronizeTrigger();
                }
            });
            document.addEventListener("close", synchronizeTrigger, true);
            document.addEventListener("keydown", event => {
                if (!event.isComposing && (event.ctrlKey || event.metaKey) && event.key.toLowerCase() === "k") {
                    if (document.querySelector("dialog[open]:not(#workspace-command)")) return;
                    event.preventDefault();
                    openDialog("workspace-command");
                }
            });
            mobile.addEventListener("change", event => {
                if (!event.matches) {
                    document.getElementById("workspace-mobile-nav")?.close();
                    synchronizeTrigger();
                }
            });
            // Dismiss only when a complete pointer gesture stays on the backdrop.
            let backdropDialog = null;
            const outside = (dialog, event) => {
                const rect = dialog.getBoundingClientRect();
                return event.clientX < rect.left || event.clientX > rect.right || event.clientY < rect.top || event.clientY > rect.bottom;
            };
            document.addEventListener("pointerdown", event => {
                backdropDialog = event.target instanceof HTMLDialogElement && outside(event.target, event) ? event.target : null;
            });
            document.addEventListener("pointerup", event => {
                if (backdropDialog && event.target === backdropDialog && outside(backdropDialog, event)) backdropDialog.close();
                backdropDialog = null;
            });
            const shortcut = document.querySelector(".command-button-kbd");
            if (shortcut && /Mac|iPhone|iPad/.test(navigator.platform)) shortcut.textContent = "⌘ K";
        }
    };
})();
