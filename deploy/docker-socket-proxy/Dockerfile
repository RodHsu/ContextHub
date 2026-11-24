FROM alpine:3.22@sha256:14358309a308569c32bdc37e2e0e9694be33a9d99e68afb0f5ff33cc1f695dce

ARG ALPINE_MIRROR=
RUN set -eu; \
    set --; \
    if [ -n "$ALPINE_MIRROR" ]; then \
        mirror="${ALPINE_MIRROR%/}"; \
        case "$mirror" in \
            https://?*) ;; \
            *) echo 'ALPINE_MIRROR must be an HTTPS mirror root' >&2; exit 1 ;; \
        esac; \
        release="$(cut -d. -f1,2 /etc/alpine-release)"; \
        set -- --repositories-file /dev/null \
            --repository "$mirror/v$release/main" \
            --repository "$mirror/v$release/community"; \
    fi; \
    apk "$@" upgrade --no-cache; \
    apk "$@" add --no-cache haproxy

ENTRYPOINT ["haproxy"]
