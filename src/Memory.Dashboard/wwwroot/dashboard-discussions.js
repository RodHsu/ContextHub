window.contextHubDiscussions = {
    scrollToFirstUnreadOrLatest(threadId) {
        if (!threadId) {
            return null;
        }

        const threadSelector = `.discussion-message[data-thread-id="${CSS.escape(threadId)}"]`;
        const unreadSelector = `${threadSelector}[data-unread="true"]`;
        const target = document.querySelector(unreadSelector)
            || Array.from(document.querySelectorAll(threadSelector)).at(-1);
        if (!target) {
            return null;
        }

        const stream = target.closest(".discussion-message-stream");
        if (stream) {
            const targetKind = target.dataset.unread === "true" ? "unread" : "latest";
            stream.dataset.lastDiscussionScroll = target.dataset.messageId || "";
            stream.dataset.discussionScrollTarget = targetKind;
        }

        target.scrollIntoView({ block: "center", inline: "nearest", behavior: "instant" });
        return target.dataset.unread === "true" ? "unread" : "latest";
    },

    isMessageStreamAtBottom(threadId) {
        if (!threadId) {
            return false;
        }

        const message = document.querySelector(`.discussion-message[data-thread-id="${CSS.escape(threadId)}"]`);
        const stream = message?.closest(".discussion-message-stream");
        return !!stream && stream.scrollHeight - stream.scrollTop - stream.clientHeight <= 2;
    }
};
