# Build stage: compile the Go init binary statically.
FROM golang:1.23-alpine AS builder
WORKDIR /src
COPY go.mod ./
COPY init.go ./
RUN CGO_ENABLED=0 GOOS=linux GOARCH=amd64 go build \
    -trimpath \
    -ldflags='-s -w -extldflags=-static' \
    -o /out/init ./init.go

# filebrowser: pinned, sha256-verified static binary (no apk package exists,
# upstream ships release tarballs only). Fetched in the builder stage so curl
# never lands in the final image — only the extracted binary does. See
# init.go's startFileBrowserServer for why it's fronted by our own tiny
# reverse-proxy/auto-provisioning shim instead of being exposed directly.
ARG FILEBROWSER_VERSION=v2.63.17
ARG FILEBROWSER_SHA256=d463799e80bebff70f2e6faddf8628184b11248722cd0298ca3fd340f98914e9
RUN apk add --no-cache curl \
    && curl -fsSL -o /tmp/filebrowser.tar.gz \
       "https://github.com/filebrowser/filebrowser/releases/download/${FILEBROWSER_VERSION}/linux-amd64-filebrowser.tar.gz" \
    && echo "${FILEBROWSER_SHA256}  /tmp/filebrowser.tar.gz" | sha256sum -c - \
    && tar -xzf /tmp/filebrowser.tar.gz -C /out filebrowser \
    && chmod +x /out/filebrowser

# Final stage: minimal Alpine + the init binary at /usr/local/bin/mvm-init.
# Alpine provides /dev, /proc, /sys mountpoints + busybox utilities for debugging.
#
# ttyd: gives every stock agent a shell, reachable through the same generic
# /agents/<name>/p/<port>/... ingress pathway as any other port — no platform
# auth carve-out, no session tokens (see app/src/Middleware/AgentsIngressMiddleware.cs
# design note). Deliberately run WITHOUT --base-path: ttyd's own client-side JS
# builds its WebSocket URL from the browser's actual location.pathname at
# runtime, and the platform's ingress middleware always strips the
# /agents/<name>/p/<port> prefix before forwarding — so ttyd must see bare
# paths (/, /ws, /token) to match what it's actually sent. Verified live
# (docker run alpine:3.20 + ttyd 1.7.7): --base-path makes ttyd 404 on the
# bare paths the proxy forwards; without it, a real WS upgrade to /ws
# succeeds (101 Switching Protocols, Sec-WebSocket-Protocol: tty echoed back).
#
# IMPORTANT: We do NOT install at /sbin/init during the Docker build. Docker
# BuildKit uses /sbin/init as the build-container's init when one exists, which
# would execute our binary at every RUN step. Instead, the rootfs Makefile
# symlinks /sbin/init → /usr/local/bin/mvm-init in the staging step, after
# `docker export` has produced the tar.
FROM alpine:3.20
RUN apk add --no-cache busybox-static ttyd
COPY --from=builder /out/init /usr/local/bin/mvm-init
COPY --from=builder /out/filebrowser /usr/local/bin/filebrowser
RUN chmod +x /usr/local/bin/mvm-init /usr/local/bin/filebrowser
