# Build stage: compile the shared Go init binary — see rootfs/init.go's header
# and the ubuntu Dockerfile's comment; the identical binary boots every
# distro, this one included.
FROM golang:1.23-alpine AS builder
WORKDIR /src
COPY go.mod ./
COPY init.go ./
RUN CGO_ENABLED=0 GOOS=linux GOARCH=amd64 go build \
    -trimpath \
    -ldflags='-s -w -extldflags=-static' \
    -o /out/init ./init.go

# hermes-authproxy: distro-specific (unlike init.go), so it's a separate
# source file/binary built here rather than folded into the shared init.go —
# see distros/hermes/authproxy.go's header for what it does and why.
COPY distros/hermes/authproxy.go ./authproxy.go
RUN CGO_ENABLED=0 GOOS=linux GOARCH=amd64 go build \
    -trimpath \
    -ldflags='-s -w -extldflags=-static' \
    -o /out/hermes-authproxy ./authproxy.go

# filebrowser: pinned, sha256-verified static binary — see the alpine
# Dockerfile's comment for why it's fetched in the builder stage.
ARG FILEBROWSER_VERSION=v2.63.17
ARG FILEBROWSER_SHA256=d463799e80bebff70f2e6faddf8628184b11248722cd0298ca3fd340f98914e9
RUN apk add --no-cache curl \
    && curl -fsSL -o /tmp/filebrowser.tar.gz \
       "https://github.com/filebrowser/filebrowser/releases/download/${FILEBROWSER_VERSION}/linux-amd64-filebrowser.tar.gz" \
    && echo "${FILEBROWSER_SHA256}  /tmp/filebrowser.tar.gz" | sha256sum -c - \
    && tar -xzf /tmp/filebrowser.tar.gz -C /out filebrowser \
    && chmod +x /out/filebrowser

# hermes-base: JUST the slow part (apt deps + the Hermes installer — several
# minutes: apt packages, uv-managed Python 3.11, Node 22, cloning the
# hermes-agent repo, its Python venv + npm deps) in its OWN stage, with
# NOTHING else added to it. Docker only invalidates a layer's cache if that
# exact instruction (and everything before it in the SAME stage) changes —
# by keeping this stage's only inputs the base image + these two RUN
# instructions, editing on-boot.sh/authproxy.go/the final COPY steps below
# can never invalidate it again. (Before this split, all of that lived in
# one final-stage RUN — the ~350 MiB cleanup line added after the first
# build forced a full from-scratch reinstall, since it was textually part of
# the same instruction as the installer itself.) Re-run only when you
# actually change the apt package list or the installer invocation/version.
#
# --skip-browser: this is a headless server image with no display, so the
# installer must not try to pull in Playwright/Chromium for a browser-driven
# OAuth flow that has nowhere to render.
#
# iproute2/ttyd/ca-certificates/curl: same as the ubuntu distro (ip for
# init.go's network setup, ttyd for shell access, ca-certificates for TLS to
# OpenRouter, curl to run the installer). git/xz-utils: Hermes' own installer
# dependencies (repo clone + archive extraction).
#
# No official prebuilt Hermes image/binary exists to COPY --from instead
# (checked: Nous Research ships a desktop installer + this shell script only,
# nothing on a container registry) — unlike filebrowser above, this really is
# the only distribution path, so the win here is caching it, not skipping it.
FROM ubuntu:24.04 AS hermes-base
RUN apt-get update \
    && apt-get install -y --no-install-recommends \
        iproute2 ttyd ca-certificates curl git xz-utils \
    && rm -rf /var/lib/apt/lists/*
RUN curl -fsSL https://hermes-agent.nousresearch.com/install.sh | bash -s -- --skip-browser \
    && rm -rf /root/.cache \
              /usr/local/lib/hermes-agent/.git \
              /usr/local/lib/hermes-agent/tests \
              /usr/local/lib/hermes-agent/website \
              /usr/local/lib/hermes-agent/infographic
# ^ ~350 MiB of pure build-time/dev weight that's never read at runtime:
# .git (repo history), tests/ (not imported by hermes_cli), website/ (the
# Docusaurus docs site source, unrelated to the running agent), infographic/
# (marketing assets), and uv/pip's download cache at /root/.cache. (This
# cleanup is its own RUN, layered on top of the apt-get RUN above — not
# appended to the installer's RUN itself — so a future tweak to JUST the
# cleanup list doesn't force apt-get + the installer to redo too; only the
# installer's own RUN and anything after it re-runs.)

# Pre-build the dashboard's web frontend HERE, once, at image-build time —
# NOT letting `hermes serve` build it lazily on first boot like the stock
# install would. Two independent reasons this matters:
#
#  1. Its actual dependency tree (node_modules) is ~262 MiB while its BUILT
#     OUTPUT (hermes_cli/web_dist) is ~3 MiB — building once here and then
#     deleting node_modules keeps the tree, not the toolchain, in the image.
#  2. `hermes serve` runs as the unprivileged 'ubuntu' user at runtime (see
#     on-boot.sh), but Node.js only got installed under /root/.hermes/node
#     by the installer above (which ran as root) — and /root itself is
#     0700, unreachable to ubuntu no matter what's under it. A lazy build
#     attempted as ubuntu at runtime fails outright ("Web UI frontend not
#     built and npm is not available"). Building as root NOW, while
#     Node is actually reachable, and shipping the output at
#     hermes_cli/web_dist — a normal /usr/local/lib path, not under
#     /root — sidesteps the per-user-home split entirely: default file
#     perms from this RUN (root:root, 0755/0644) are world-readable, so
#     ubuntu can serve it fine at runtime. on-boot.sh passes `--skip-build`
#     to `hermes serve` so it trusts this prebuilt dist instead of
#     re-checking for npm (which, as ubuntu, it still wouldn't find).
#
# _build_web_ui is called directly (not `hermes serve` itself) so this step
# only builds the frontend — no port binding, no provider/model config
# needed yet. Uses the already-installed venv's own python (has every
# hermes_cli import already resolved) rather than a fresh interpreter.
COPY distros/hermes/build-web-ui.py /tmp/build-web-ui.py
RUN cd /usr/local/lib/hermes-agent \
    && PATH="/root/.hermes/node/bin:/root/.hermes/bin:${PATH}" \
       HOME=/root HERMES_HOME=/root/.hermes \
       /usr/local/lib/hermes-agent/venv/bin/python3 /tmp/build-web-ui.py \
    && test -f hermes_cli/web_dist/index.html \
    && rm -rf node_modules \
    && rm -f /tmp/build-web-ui.py

# Final stage: mvm-init + Hermes Agent assembled onto hermes-base. Only
# these lightweight COPY/chmod steps live here, so editing any of them never
# touches the expensive stage above.
#
# The agent is launched by /etc/mvm/on-boot.sh, which init.go execs
# generically for any distro shipping that path (see init.go's "distro boot
# hook" section) — init.go itself has zero Hermes-specific code. on-boot.sh
# reads the OPENROUTER_API_KEY / OPENROUTER_BASE_URL / OPENROUTER_MODEL
# tenant secrets that init.go already wrote to /run/secrets (Firecracker MMDS
# — see AgentSecretsService / the Console's per-agent Secrets panel) and uses
# them to configure Hermes' OpenRouter provider before starting it, then runs
# `hermes serve` bound to 127.0.0.1:9120 (loopback — Hermes' own dashboard
# auth gate never engages there) fronted by hermes-authproxy on 0.0.0.0:9119,
# which trusts the platform's already-authenticated X-Mvm-User-Email
# ingress header — same loopback+trusted-proxy shape init.go already uses
# for filebrowser, so Hermes needs no dashboard-auth plugin of its own.
FROM hermes-base
COPY --from=builder /out/init /usr/local/bin/mvm-init
COPY --from=builder /out/filebrowser /usr/local/bin/filebrowser
COPY --from=builder /out/hermes-authproxy /usr/local/bin/hermes-authproxy
RUN chmod +x /usr/local/bin/mvm-init /usr/local/bin/filebrowser /usr/local/bin/hermes-authproxy

# IMPORTANT: same /sbin/init caveat as the alpine/ubuntu Dockerfiles — do NOT
# install at /sbin/init during the Docker build; BuildKit would pick it up as
# the build-container's init. The rootfs Makefile symlinks it after `docker
# export`.
COPY distros/hermes/on-boot.sh /etc/mvm/on-boot.sh
RUN chmod +x /etc/mvm/on-boot.sh

# Static service manifest (see init.go's loadServicesManifest) — lets the
# Console render a "Hermes Dashboard" link instead of a bare port number,
# without hermes-authproxy's port 9119 having to be hardcoded anywhere in
# console/src.
COPY distros/hermes/services.json /etc/mvm/services.json
