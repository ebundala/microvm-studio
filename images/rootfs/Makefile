# Build a rootfs image for one distro.
#
#   docker build -f distros/$(DISTRO)/Dockerfile  → exports a tar of the filesystem
#   mkfs.ext4 -d <staging-dir>                     → builds the ext4 image directly from the dir
#
# Every distro shares the same init.go/go.mod (PID-1 init, pure Go/syscalls,
# no distro-specific assumptions) — only distros/<name>/Dockerfile differs
# (base image + how it gets ttyd/iproute2 on board). Add a new distro by
# adding a new distros/<name>/Dockerfile; nothing else here needs to change.
#
#   make rootfs                   # DISTRO=alpine (default)
#   make rootfs DISTRO=ubuntu
#   make rootfs-all               # builds every distro under distros/
#
# Image size: computed per-build from the ACTUAL staged content (post
# Dockerfile, post any distro's own cleanup) plus HEADROOM_MB, rather than a
# guessed fixed constant — the rootfs is mounted read-only at boot and every
# agent's writes land on its own separate persistent data disk (see
# init.go's switchToWritableOverlayRoot), so this file never grows after
# build time and doesn't need padding for that. A fixed guess either wastes
# space (e.g. a flat 4096 MiB pad on a ~1.5 GiB image — pure dead weight
# that then has to be uploaded to the image catalog) or, guessed too small,
# fails mkfs outright ("Could not allocate block"). Pass SIZE_MB=<n>
# explicitly to override the computed value for a specific build.
HEADROOM_MB ?= 100

BUILD_DIR  ?= ../build
DISTRO     ?= alpine
IMAGE_TAG  := mvm-stock-rootfs-$(DISTRO):dev
STAGING    := $(BUILD_DIR)/rootfs-staging-$(DISTRO)
OUT        := $(BUILD_DIR)/rootfs-$(DISTRO).ext4
DISTROS    := $(notdir $(wildcard distros/*))

.PHONY: all rootfs-all clean
all: $(OUT)

rootfs-all:
	@for d in $(DISTROS); do $(MAKE) DISTRO=$$d BUILD_DIR=$(BUILD_DIR) all; done

$(OUT): $(wildcard distros/$(DISTRO)/*) init.go go.mod
	@test -f distros/$(DISTRO)/Dockerfile || { echo "no such distro: $(DISTRO) (looked for distros/$(DISTRO)/Dockerfile)"; exit 1; }
	@mkdir -p $(BUILD_DIR)
	@echo ">>> Building Docker image: $(IMAGE_TAG)"
	docker build -f distros/$(DISTRO)/Dockerfile -t $(IMAGE_TAG) .

	@echo ">>> Exporting rootfs to $(STAGING)"
	@rm -rf $(STAGING) && mkdir -p $(STAGING)
	docker create --name mvm-rootfs-tmp-$(DISTRO) $(IMAGE_TAG) >/dev/null
	docker export mvm-rootfs-tmp-$(DISTRO) | tar -xf - -C $(STAGING)
	docker rm mvm-rootfs-tmp-$(DISTRO) >/dev/null

	@echo ">>> Wiring /sbin/init → mvm-init"
	# We deliberately keep the Go binary at /usr/local/bin/mvm-init in the
	# Docker image (otherwise BuildKit picks it up as the build-container's init
	# and runs it on every RUN step). Symlink it as /sbin/init now, after export.
	# Replace any existing /sbin/init (busybox-static on Alpine, none on Ubuntu).
	rm -f $(STAGING)/sbin/init
	ln -sf /usr/local/bin/mvm-init $(STAGING)/sbin/init

	@if [ "$(origin SIZE_MB)" = "command line" ]; then \
		size_mb="$(SIZE_MB)"; \
		echo ">>> SIZE_MB=$${size_mb} MiB (explicit override)"; \
	else \
		content_mb=$$(du -sm $(STAGING) | cut -f1); \
		size_mb=$$((content_mb + $(HEADROOM_MB))); \
		echo ">>> Staged content: $${content_mb} MiB + $(HEADROOM_MB) MiB headroom = $${size_mb} MiB"; \
	fi; \
	echo ">>> Building $${size_mb} MiB ext4 image at $(OUT)"; \
	rm -f $(OUT); \
	truncate -s "$${size_mb}M" $(OUT); \
	mkfs.ext4 -F -L mvm-rootfs -d $(STAGING) $(OUT)
	# `mkfs.ext4 -d <dir>` (e2fsprogs >= 1.43) populates the FS from a staging dir
	# without needing root or loopback mount. Fast and reproducible.
	@echo ">>> Rootfs:  $(OUT)  ($$(du -h $(OUT) | awk '{print $$1}'))"
	@echo "    SHA256:  $$(sha256sum $(OUT) | awk '{print $$1}')"

clean:
	rm -rf $(BUILD_DIR)/rootfs-staging-* $(BUILD_DIR)/rootfs-*.ext4
	-for d in $(DISTROS); do docker rmi mvm-stock-rootfs-$$d:dev 2>/dev/null || true; done
