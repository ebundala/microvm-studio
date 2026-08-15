#!/bin/sh
# Boot hook for the "hermes" rootfs distro — run by init.go's runBootHook
# (see init.go's "distro boot hook" section) once tenant secrets are already
# under /run/secrets. Configures Hermes Agent (Nous Research,
# https://hermes-agent.nousresearch.com) to use OpenRouter and launches its
# headless backend, so every tenant that picks this rootfs boots straight
# into an AI agent instead of the stock hello-world server.
#
# PID 1 (init.go) execs this script with an EMPTY environment — no PATH, no
# HOME — so everything needed below is set explicitly rather than assumed.
set -eu
export PATH=/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin

# `hermes serve` itself runs as the unprivileged 'ubuntu' user (UID 1000,
# baked into the ubuntu:24.04 base image — see /etc/passwd), NOT root.
# Hermes' own startup security audit (hermes_cli/security_audit_startup.py)
# explicitly flags running as root: "the agent's terminal/file tools execute
# with full root privileges — a single prompt-injection or exposed endpoint
# is a full host compromise. Run Hermes as an unprivileged user." This
# script itself still runs as root (PID 1 execs it that way, and reading
# /run/secrets requires it — those files are root-only, see init.go's
# writeSecrets), but everything it writes for Hermes below goes straight
# into the ubuntu user's own home + gets chowned to it, and the actual
# `hermes serve` process is `su`'d into that user before it ever starts.
HERMES_USER=ubuntu
HERMES_USER_HOME=/home/ubuntu
export HERMES_HOME="$HERMES_USER_HOME/.hermes"
mkdir -p "$HERMES_HOME"

# Tenant-managed secrets (set via the agent's Secrets panel in the Console,
# delivered here through Firecracker MMDS by init.go's writeSecrets) land in
# this dotenv file; `set -a` exports every var it defines so the ${VAR}
# expansions below (this shell's own, at write time — see the config.yaml
# heredoc further down) see them. Re-run on every boot hook invocation,
# which only happens once at boot — a secret changed later is picked up by
# init.go's background MMDS poller rewriting /run/secrets, but this script
# (and the `hermes serve` process it execs) does not itself watch for that
# change; restart the agent to pick up a changed URL/model/key.
SECRETS_ENV=/run/secrets/env
if [ -f "$SECRETS_ENV" ]; then
    set -a
    . "$SECRETS_ENV"
    set +a
fi

# Defaults so the agent comes up usable even before a tenant sets anything:
# OpenRouter's real endpoint, and "openrouter/free" — OpenRouter's own
# official meta-model ("Free Models Router") that randomly routes across
# whichever free-tier models are currently up, filtered for tool-call
# support (confirmed via GET https://openrouter.ai/api/v1/models: its
# supported_parameters includes "tools"/"tool_choice", which an agent needs).
# A single pinned free model (e.g. nousresearch/hermes-3-llama-3.1-405b:free,
# used here previously) is a worse default specifically BECAUSE it's
# pinned — free-tier models get deprecated/rate-limited/taken down
# individually and unpredictably, silently breaking every agent still
# defaulted to that one exact id; the router sidesteps that by construction.
# A tenant overrides either by setting the OPENROUTER_BASE_URL /
# OPENROUTER_MODEL secret from the Console.
: "${OPENROUTER_BASE_URL:=https://openrouter.ai/api/v1}"
: "${OPENROUTER_MODEL:=openrouter/free}"

# Schema is FLAT under `model:` — confirmed against Hermes' own shipped
# cli-config.yaml.example, not the earlier (wrong) assumption of a nested
# `providers.<name>.*` block, which Hermes' config loader never reads at
# all. Values are plain strings expanded by THIS shell at write time (no
# `${VAR}` inside the YAML itself — fast_safe_load is a bare YAML parser
# with no env-substitution step, confirmed by grepping hermes_cli/config.py
# and utils.py for any expandvars-equivalent: there is none).
cat > "$HERMES_HOME/config.yaml" <<EOF
model:
  provider: openrouter
  default: "${OPENROUTER_MODEL}"
  base_url: "${OPENROUTER_BASE_URL}"
EOF

# The API key is deliberately NOT written into config.yaml (not even as a
# literal value) — it goes in HERMES_HOME/.env instead, which
# hermes_cli/env_loader.py loads via python-dotenv's load_dotenv() at
# startup and hermes_cli/models.py's _resolve_openrouter_api_key() (and the
# rest of the "requires: OPENROUTER_API_KEY" convention throughout the
# example config) reads straight off os.environ — the .env path is the
# documented, working one ("Environment variables in .env take precedence",
# per the example file's own header), not a YAML field.
if [ -n "${OPENROUTER_API_KEY:-}" ]; then
    printf 'OPENROUTER_API_KEY=%s\n' "${OPENROUTER_API_KEY}" > "$HERMES_HOME/.env"
    chmod 600 "$HERMES_HOME/.env"
else
    echo "[hermes-boot] WARN: OPENROUTER_API_KEY secret not set — set it from this agent's Secrets panel in the Console, then restart the agent"
fi

# Everything above was written as root (root:root, since that's who created
# it) — hand it to the ubuntu user before ubuntu ever tries to read it.
# Recursive because a re-run (agent restart) may find files from a PREVIOUS
# boot already there; -R makes this idempotent regardless of what's already
# under $HERMES_HOME and who owns it.
chown -R "$HERMES_USER:$HERMES_USER" "$HERMES_HOME"

echo "[hermes-boot] configured provider=openrouter model=${OPENROUTER_MODEL} base_url=${OPENROUTER_BASE_URL}"

# --host 127.0.0.1 (NOT 0.0.0.0): Hermes' own dashboard auth gate only
# engages on a non-loopback bind — on loopback it serves with no auth
# provider required at all (confirmed by `hermes serve`'s own startup
# diagnostic when it refuses a 0.0.0.0 bind with none configured). Rather
# than configure a Hermes-specific login (password or OAuth) that the
# tenant would have to click through separately from the platform's own
# auth, this keeps `hermes serve` unreachable from outside the guest and
# lets hermes-authproxy (below) be the only thing actually exposed —
# mirroring how init.go already runs filebrowser loopback-only and fronts
# it with a trusting proxy of its own.
# --no-open: no browser/display exists inside this guest to open one.
#
# `su -s /bin/sh "$HERMES_USER" -c '...'`: drops from root to the
# unprivileged ubuntu user for the actual long-running agent process — the
# part that executes arbitrary LLM-directed terminal/file tool calls, i.e.
# exactly what Hermes' own security audit warns about running as root. `su`
# (unlike a bare setuid syscall) also resets the environment to whatever the
# target user's shell would normally see, so HOME/HERMES_HOME are set
# explicitly inside the -c command rather than relying on anything from
# this root shell leaking through.
#
# --skip-build: the Dockerfile pre-builds the dashboard frontend at image
# build time (as root, when Node.js — installed under /root/.hermes/node —
# is actually reachable) and ships it at hermes_cli/web_dist, a world-
# readable /usr/local/lib path. Without --skip-build, `hermes serve` tries
# to lazily rebuild it on every start, and as the ubuntu user it can't even
# see Node at all (/root is 0700 — unreachable regardless of what's under
# it), so it would hard-fail with "Web UI frontend not built and npm is not
# available" every single boot.
su -s /bin/sh "$HERMES_USER" -c \
    "export HOME='$HERMES_USER_HOME' HERMES_HOME='$HERMES_HOME'; exec /usr/local/bin/hermes serve --host 127.0.0.1 --port 9120 --no-open --skip-build" &

# hermes-authproxy: reachable via the platform's generic
# /vms/<name>/p/9119/... ingress pathway like any other port. It forwards
# to the loopback hermes serve above ONLY when the request already carries
# X-Mvm-User-Email — i.e. the ingress proxy already authenticated
# this tenant member for gated ingress mode — so Hermes' dashboard ends up
# gated by the platform's own auth instead of a separate Hermes login, with
# no Hermes-side auth plugin needed. exec (not another backgrounded &):
# keeps this the direct child init.go's runBootHook is watching via
# cmd.Wait(), so a crash surfaces as a logged boot-hook exit rather than
# silently vanishing.
exec /usr/local/bin/hermes-authproxy
