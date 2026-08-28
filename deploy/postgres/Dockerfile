FROM golang:1.26-bookworm@sha256:53eeac89074db483fdf0ab3be1df32bf6e47562263d2d0d6baa7f26acb4957dd AS gosu-build

RUN GOBIN=/out CGO_ENABLED=0 go install -trimpath -ldflags="-s -w" github.com/tianon/gosu@1.19

FROM pgvector/pgvector:pg17-trixie@sha256:a74b9af952f5609c090120bf938b0c8bca56c33ed9fb05643fd9fceec52c4a08

COPY --from=gosu-build /out/gosu /usr/local/bin/gosu
RUN gosu --version \
    && gosu nobody true
