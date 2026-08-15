# Pre-built kernel from Firecracker's CI S3 bucket.
# These are signed, reproducible, and have all required virtio drivers.
# Source: https://github.com/firecracker-microvm/firecracker/blob/main/docs/getting-started.md

BUILD_DIR ?= ../build
ARCH := $(shell uname -m)
KERNEL_VERSION := 5.10.225
KERNEL_URL := https://s3.amazonaws.com/spec.ccfc.min/firecracker-ci/v1.11/$(ARCH)/vmlinux-$(KERNEL_VERSION)

OUT := $(BUILD_DIR)/vmlinux.bin

.PHONY: all clean
all: $(OUT)

$(OUT):
	@mkdir -p $(BUILD_DIR)
	@echo "Downloading Firecracker pre-built kernel $(KERNEL_VERSION) for $(ARCH)..."
	curl -fsSL -o $(OUT).tmp "$(KERNEL_URL)"
	mv $(OUT).tmp $(OUT)
	@echo ">>> Kernel:  $(OUT)  ($$(du -h $(OUT) | awk '{print $$1}'))"
	@echo "    SHA256:  $$(sha256sum $(OUT) | awk '{print $$1}')"

clean:
	rm -f $(OUT) $(OUT).tmp
