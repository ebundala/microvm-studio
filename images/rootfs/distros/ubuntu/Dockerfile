# Build stage: compile the Go init binary statically. Same binary boots any
# distro — see init.go's header comment; it's pure Go/syscalls, no apk/apt
# shell-outs, so only the base image + packages below change per distro.
FROM golang:1.23-alpine AS builder
WORKDIR /src
COPY go.mod ./
COPY init.go ./
RUN CGO_ENABLED=0 GOOS=linux GOARCH=amd64 go build \
    -trimpath \
    -ldflags='-s -w -extldflags=-static' \
    -o /out/init ./init.go

# filebrowser: pinned, sha256-verified static binary (no apt package exists,
# upstream ships release tarballs only) — see the Alpine Dockerfile's comment
# for why it's fetched here rather than in the final stage.
ARG FILEBROWSER_VERSION=v2.63.17
ARG FILEBROWSER_SHA256=d463799e80bebff70f2e6faddf8628184b11248722cd0298ca3fd340f98914e9
RUN apk add --no-cache curl \
    && curl -fsSL -o /tmp/filebrowser.tar.gz \
       "https://github.com/filebrowser/filebrowser/releases/download/${FILEBROWSER_VERSION}/linux-amd64-filebrowser.tar.gz" \
    && echo "${FILEBROWSER_SHA256}  /tmp/filebrowser.tar.gz" | sha256sum -c - \
    && tar -xzf /tmp/filebrowser.tar.gz -C /out filebrowser \
    && chmod +x /out/filebrowser

# Final stage: minimal Ubuntu + the init binary at /usr/local/bin/mvm-init.
#
# iproute2: provides /sbin/ip, which init.go execs for interface/route setup
# (the Alpine image gets the same via busybox-static).
# ttyd: shell access, same ingress path as the Alpine image — see the
# Alpine Dockerfile's comment for why it's run without --base-path.
# filebrowser: web file manager, same ingress path again — see init.go's
# startFileBrowserServer for the auto-provisioning shim in front of it.
#
# IMPORTANT: same /sbin/init caveat as the Alpine build — do NOT install at
# /sbin/init during the Docker build; BuildKit would pick it up as the
# build-container's init. The rootfs Makefile symlinks it after `docker export`.
FROM ubuntu:24.04
RUN apt-get update \
    && apt-get install -y --no-install-recommends iproute2 ttyd ca-certificates \
    && rm -rf /var/lib/apt/lists/*
COPY --from=builder /out/init /usr/local/bin/mvm-init
COPY --from=builder /out/filebrowser /usr/local/bin/filebrowser
RUN chmod +x /usr/local/bin/mvm-init /usr/local/bin/filebrowser
