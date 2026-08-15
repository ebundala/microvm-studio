#!/usr/bin/env bash
# install.sh — the one privileged step MicroVM Studio needs.
#
#   sudo ./packaging/install.sh            # build + install helper, policy, dirs
#   sudo ./packaging/install.sh --debug    # skip AOT, install a framework-dependent build
#   sudo ./packaging/install.sh --check    # preflight only, change nothing
#   sudo ./packaging/install.sh --uninstall
#
# After this, the app itself runs unprivileged and prompts via polkit only when
# it needs the helper.
#
# The helper MUST land on an absolute, root-owned path: polkit >= 0.106 refuses
# to exec anything else, so running it out of the build directory cannot work.

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_DIR="$(cd "$SCRIPT_DIR/.." && pwd)"

HELPER_PROJECT="$REPO_DIR/src/MicroVMStudio.Helper/MicroVMStudio.Helper.csproj"
INSTALL_DIR="/usr/libexec/microvm-studio"
HELPER_BIN="$INSTALL_DIR/helper"
POLICY_SRC="$SCRIPT_DIR/com.microvmstudio.helper.policy"
POLICY_DEST="/usr/share/polkit-1/actions/com.microvmstudio.helper.policy"
STATE_DIR="/var/lib/microvm-studio"
SOCKET_PATH="/run/microvm-studio-helper.sock"

MODE="install"
USE_DEBUG=false
for arg in "$@"; do
    case "$arg" in
        --debug)     USE_DEBUG=true ;;
        --check)     MODE="check" ;;
        --uninstall) MODE="uninstall" ;;
        -h|--help)   sed -n '2,14p' "$0"; exit 0 ;;
        *) echo "unknown argument: $arg" >&2; exit 2 ;;
    esac
done

info() { printf '\033[1;34m>>>\033[0m %s\n' "$*"; }
warn() { printf '\033[1;33mWARN\033[0m %s\n' "$*" >&2; }
fail() { printf '\033[1;31mERROR\033[0m %s\n' "$*" >&2; exit 1; }

require_root() {
    [[ "$(id -u)" -eq 0 ]] || fail "must run as root. Try: sudo $0 $*"
}

# .NET installs to non-system locations on Linux and sudo strips PATH, so the
# absolute path has to be resolved explicitly and handed to the build user.
resolve_dotnet() {
    if [[ -n "${DOTNET_BIN:-}" && -x "${DOTNET_BIN}" ]]; then echo "$DOTNET_BIN"; return 0; fi

    local user_home
    if [[ -n "${SUDO_USER:-}" ]]; then
        user_home=$(getent passwd "$SUDO_USER" | cut -d: -f6)
    else
        user_home="${HOME:-/root}"
    fi

    local c
    for c in "$user_home/.dotnet/active/dotnet" "$user_home/.dotnet/dotnet" \
             /usr/local/share/dotnet/dotnet /usr/share/dotnet/dotnet \
             /usr/local/bin/dotnet /usr/bin/dotnet; do
        [[ -x "$c" ]] && { echo "$c"; return 0; }
    done

    if [[ -d "$user_home/.dotnet" ]]; then
        local found
        found=$(ls -1 "$user_home"/.dotnet/v*/dotnet 2>/dev/null | sort -r | head -1)
        [[ -n "$found" && -x "$found" ]] && { echo "$found"; return 0; }
    fi
    return 1
}

preflight() {
    local ok=true

    info "Checking host prerequisites..."

    if [[ -e /dev/kvm ]]; then
        echo "  [ok]   /dev/kvm present"
    else
        echo "  [FAIL] /dev/kvm missing — this host cannot run Firecracker."
        echo "         Enable virtualization in BIOS, or load kvm_intel / kvm_amd."
        ok=false
    fi

    if [[ -f /sys/fs/cgroup/cgroup.controllers ]]; then
        echo "  [ok]   cgroup v2 mounted"
    else
        echo "  [FAIL] cgroup v2 not mounted at /sys/fs/cgroup."
        echo "         jailer needs it; boot with systemd.unified_cgroup_hierarchy=1."
        ok=false
    fi

    local b
    for b in /usr/local/bin/firecracker /usr/local/bin/jailer; do
        if [[ -x "$b" ]]; then
            echo "  [ok]   $b"
        else
            echo "  [FAIL] $b not found or not executable."
            echo "         Get both from https://github.com/firecracker-microvm/firecracker/releases"
            ok=false
        fi
    done

    for b in /sbin/ip /sbin/iptables /usr/sbin/mkfs.ext4 /usr/bin/truncate /usr/bin/du /usr/bin/stat; do
        if [[ -x "$b" ]]; then
            echo "  [ok]   $b"
        else
            echo "  [FAIL] $b not found (install iproute2 / iptables / e2fsprogs / coreutils)."
            ok=false
        fi
    done

    if command -v pkexec >/dev/null 2>&1; then
        echo "  [ok]   pkexec available"
    else
        echo "  [warn] pkexec not found — install polkit, or start the helper with sudo."
    fi

    if command -v docker >/dev/null 2>&1; then
        echo "  [ok]   docker available (needed to build rootfs images)"
    else
        echo "  [warn] docker not found — image builds will fail until it is installed."
    fi

    $ok || return 1
    return 0
}

do_install() {
    require_root

    preflight || warn "Preflight reported problems; continuing, but the app may not work."

    local dotnet_bin
    dotnet_bin=$(resolve_dotnet) || fail "cannot find 'dotnet'. Set DOTNET_BIN=/path/to/dotnet."

    local builder_user="${SUDO_USER:-root}"
    local builder_home
    builder_home=$(getent passwd "$builder_user" | cut -d: -f6)
    local publish_dir="$REPO_DIR/src/MicroVMStudio.Helper/publish"

    # Build as the invoking user so obj/bin and the NuGet cache don't end up
    # root-owned and break later unprivileged builds.
    if $USE_DEBUG; then
        info "Building helper (Debug, framework-dependent)..."
        sudo -u "$builder_user" -H env "DOTNET_ROOT=$(dirname "$dotnet_bin")" "HOME=$builder_home" \
            "$dotnet_bin" publish "$HELPER_PROJECT" -c Debug -o "$publish_dir" \
            -p:PublishAot=false
    else
        info "Publishing helper (Release, NativeAOT)..."
        # AOT needs a native toolchain: on Ubuntu, apt install clang zlib1g-dev
        sudo -u "$builder_user" -H env "DOTNET_ROOT=$(dirname "$dotnet_bin")" "HOME=$builder_home" \
            "$dotnet_bin" publish "$HELPER_PROJECT" -c Release -r linux-x64 \
            --self-contained true -p:PublishAot=true -o "$publish_dir"
    fi

    info "Installing helper to $HELPER_BIN"
    install -d -m 0755 "$INSTALL_DIR"
    install -m 0755 -o root -g root "$publish_dir/microvm-studio-helper" "$HELPER_BIN"
    [[ -f "$publish_dir/appsettings.json" ]] && \
        install -m 0644 -o root -g root "$publish_dir/appsettings.json" "$INSTALL_DIR/appsettings.json"

    if $USE_DEBUG; then
        # A framework-dependent build needs its whole publish tree alongside it.
        cp -r "$publish_dir"/*.dll "$INSTALL_DIR/" 2>/dev/null || true
        cp -r "$publish_dir"/*.json "$INSTALL_DIR/" 2>/dev/null || true
    fi

    info "Installing polkit policy to $POLICY_DEST"
    install -m 0644 -o root -g root "$POLICY_SRC" "$POLICY_DEST"

    info "Creating state directories under $STATE_DIR"
    install -d -m 0755 -o root -g root "$STATE_DIR" \
        "$STATE_DIR/images" "$STATE_DIR/disks" "$STATE_DIR/jails" \
        "$STATE_DIR/diagnostics"
    # Key material for secret encryption — root-only.
    install -d -m 0700 -o root -g root "$STATE_DIR/keys"

    echo
    info "Install complete."
    echo "  helper:  $HELPER_BIN"
    echo "  policy:  $POLICY_DEST"
    echo "  state:   $STATE_DIR"
    echo
    echo "Start the app normally (as your own user). It will prompt for a password"
    echo "the first time it needs the helper."
    echo
    echo "If no polkit agent is running (headless SSH, bare tiling WM), start the"
    echo "helper yourself instead:"
    echo "  sudo $HELPER_BIN"
}

do_uninstall() {
    require_root
    info "Stopping any running helper..."
    pkill -f "$HELPER_BIN" 2>/dev/null || true
    rm -f "$SOCKET_PATH"

    info "Removing $INSTALL_DIR and $POLICY_DEST"
    rm -rf "$INSTALL_DIR"
    rm -f "$POLICY_DEST"

    warn "Left $STATE_DIR in place (it holds your images and disks)."
    warn "Remove it manually if you really want to discard them:  sudo rm -rf $STATE_DIR"
}

case "$MODE" in
    check)     preflight ;;
    install)   do_install ;;
    uninstall) do_uninstall ;;
esac
