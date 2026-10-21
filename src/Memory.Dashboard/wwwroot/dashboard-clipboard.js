(function () {
    async function copyText(text) {
        if (navigator.clipboard && window.isSecureContext) {
            await navigator.clipboard.writeText(text);
            return;
        }

        const textarea = document.createElement("textarea");
        textarea.value = text;
        textarea.setAttribute("readonly", "readonly");
        textarea.style.position = "fixed";
        textarea.style.top = "-1000px";
        textarea.style.left = "-1000px";

        try {
            document.body.appendChild(textarea);
            textarea.select();
            if (!document.execCommand("copy")) {
                throw new Error("Clipboard copy failed.");
            }
        } finally {
            textarea.remove();
        }
    }

    window.contextHubDashboard = window.contextHubDashboard || {};
    window.contextHubDashboard.copyText = copyText;
})();
