# MicroVM image build — top-level entry point.
# See README.md for details.
#
#   make all                  # kernel + rootfs (DISTRO=alpine default)
#   make rootfs DISTRO=ubuntu # one specific distro
#   make rootfs-all           # every distro under rootfs/distros/

BUILD_DIR := build
DISTRO    ?= alpine

.PHONY: all kernel rootfs rootfs-all clean
all: kernel rootfs

kernel:
	$(MAKE) -C kernel BUILD_DIR=../$(BUILD_DIR)

rootfs:
	$(MAKE) -C rootfs BUILD_DIR=../$(BUILD_DIR) DISTRO=$(DISTRO)

rootfs-all:
	$(MAKE) -C rootfs BUILD_DIR=../$(BUILD_DIR) rootfs-all

clean:
	rm -rf $(BUILD_DIR)
	$(MAKE) -C kernel clean
	$(MAKE) -C rootfs clean
