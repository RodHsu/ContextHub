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

        document.body.appendChild(textarea);
        textarea.select();

        try {
            document.execCommand("copy");
        } finally {
            document.body.removeChild(textarea);
        }
    }

    window.contextHubDashboard = window.contextHubDashboard || {};
    window.contextHubDashboard.copyText = copyText;
})();
