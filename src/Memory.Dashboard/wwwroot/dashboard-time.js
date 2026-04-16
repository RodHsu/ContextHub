window.contextHubTime = {
    formatLocalTimestamp(isoValue) {
        if (!isoValue) {
            return "";
        }

        const value = new Date(isoValue);
        if (Number.isNaN(value.getTime())) {
            return isoValue;
        }

        const formatter = new Intl.DateTimeFormat(undefined, {
            year: "numeric",
            month: "2-digit",
            day: "2-digit",
            hour: "2-digit",
            minute: "2-digit",
            second: "2-digit",
            hour12: false,
            timeZoneName: "short"
        });

        const parts = formatter.formatToParts(value);
        const map = Object.fromEntries(parts.map(part => [part.type, part.value]));
        const year = map.year ?? "";
        const month = map.month ?? "";
        const day = map.day ?? "";
        const hour = map.hour ?? "";
        const minute = map.minute ?? "";
        const second = map.second ?? "";
        const zone = map.timeZoneName ?? "";

        return `${year}-${month}-${day} ${hour}:${minute}:${second} ${zone}`.trim();
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
