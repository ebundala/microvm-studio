// PID-1 init for the stock hello-world agent rootfs.
//
// Runs as the first process in the Firecracker microVM. Sequence:
//   1. Mount /proc, /sys, /dev (busybox `mount` is also available but we skip
//      execing it — using direct syscalls keeps init self-contained).
//   2. Read /proc/cmdline → extract `agent_cid=<n>` (passed by orchestrator).
//   3. Identity comes from the kernel cmdline (see fetchIdentityOverVsock's
//      comment for why the vsock path below is unused).
//   4. Fetch tenant-managed secrets from Firecracker's native MMDS
//      (fetchMmdsSecrets) and write them under /run/secrets, then keep
//      polling in the background (startMmdsSecretsPoller) so a secret set
//      or deleted from the Console after this agent already booted lands
//      under /run/secrets without requiring a restart.
//   4.5. Run this distro's boot hook if it shipped one (runBootHook,
//      /etc/mvm/on-boot.sh) — a no-op for stock alpine/ubuntu; the
//      "hermes" distro uses it to configure and launch the Hermes AI agent
//      from the secrets just written in step 4.
//   5. Spawn ttyd in the background on TCP :7681 (shell access) — see
//      startShellServer. Reachable via the platform's generic
//      /vms/<name>/p/7681/... ingress pathway like any other port; no
//      separate auth carve-out (see the ingress proxy's design note).
//      Run WITHOUT --base-path: ttyd's client JS builds its WebSocket URL
//      from the browser's actual location.pathname at runtime, and the
//      ingress middleware always strips the /vms/<name>/p/<port> prefix
//      before forwarding — ttyd must see the same bare paths (/, /ws) it
//      gets today, not a --base-path-prefixed path it would 404 on.
//   6. Listen on TCP 0.0.0.0:$PRIMARY_PORT and serve `hello from <agent_name>`.
//   7. On any unrecoverable error before step 6, sleep+reboot — the orchestrator's
//      crash detector will mark the agent BootFailed and surface the diagnostic.
//
// Statically linked: build with CGO_ENABLED=0 GOOS=linux. ~5-6 MB binary.

package main

import (
	"bufio"
	"encoding/json"
	"fmt"
	"io"
	"net"
	"net/http"
	"net/http/httputil"
	"net/url"
	"os"
	"os/exec"
	"strconv"
	"strings"
	"sync"
	"syscall"
	"time"
	"unsafe"
)

// vsock socket address family. From <linux/socket.h>: AF_VSOCK = 40.
const AF_VSOCK = 40
const HOST_CID = 2
const METADATA_PORT = 8888

// AgentLogCollectorService's TCP port on the host (app/src/Configuration/AgentsOptions.cs
// LogCollectorPort) — reachable via the tenant bridge's gateway IP, the same
// IP this VM already uses as its default route. Fixed/shared constant by
// design (mirrors METADATA_PORT above): a per-agent config knob would add
// nothing here since every agent on this build talks to the same collector.
const LOG_COLLECTOR_PORT = 9009

// logFwd is the best-effort TCP forwarder set up in main() once the network
// and identity are known; io.Discard until then.
var logFwd io.Writer = io.Discard

// logLine writes a single formatted line to the console (always) and to the
// log forwarder (best-effort). Replaces ad hoc fmt.Printf/fmt.Println calls
// so every boot/runtime message is visible in the tenant-facing Logs viewer,
// not just whatever happened to print after the forwarder connected.
func logLine(format string, args ...any) {
	msg := fmt.Sprintf(format, args...)
	fmt.Println(msg)
	fmt.Fprintln(logFwd, msg)
}

// reconnectingLogWriter maintains a best-effort TCP connection to the host's
// AgentLogCollectorService, redialing with backoff whenever the connection is
// missing or drops — including the case where this VM booted before the
// collector was listening (e.g. mid-deploy) or the host backend restarts
// later in the VM's lifetime. Earlier versions dialed once at boot and gave
// up forever on failure, which silently and permanently cut a long-running
// VM out of the Logs viewer after any backend restart. Writes are
// fire-and-forget: with no live connection, a line is simply dropped — same
// "no persistence, best-effort" trade-off as the rest of this feature.
type reconnectingLogWriter struct {
	gatewayIp    string
	vsockCid     int
	servicesJSON []byte

	mu   sync.Mutex
	conn net.Conn
}

func newReconnectingLogWriter(gatewayIp string, vsockCid int, servicesJSON []byte) *reconnectingLogWriter {
	w := &reconnectingLogWriter{gatewayIp: gatewayIp, vsockCid: vsockCid, servicesJSON: servicesJSON}
	if gatewayIp != "" {
		go w.connectLoop()
	}
	return w
}

func (w *reconnectingLogWriter) connectLoop() {
	const maxBackoff = 30 * time.Second
	backoff := time.Second
	for {
		conn := w.dial()
		if conn == nil {
			time.Sleep(backoff)
			if backoff *= 2; backoff > maxBackoff {
				backoff = maxBackoff
			}
			continue
		}
		backoff = time.Second

		w.mu.Lock()
		w.conn = conn
		w.mu.Unlock()

		// The collector never writes anything back (write-only protocol from
		// the VM's side), so a Read here just blocks until the host closes
		// the connection or it otherwise breaks — that's our reconnect signal.
		var buf [1]byte
		_, _ = conn.Read(buf[:])

		w.mu.Lock()
		if w.conn == conn {
			w.conn = nil
		}
		w.mu.Unlock()
		conn.Close()
	}
}

func (w *reconnectingLogWriter) dial() net.Conn {
	addr := fmt.Sprintf("%s:%d", w.gatewayIp, LOG_COLLECTOR_PORT)
	conn, err := net.DialTimeout("tcp", addr, 2*time.Second)
	if err != nil {
		return nil
	}
	if _, err := fmt.Fprintf(conn, "AGENT %d\n", w.vsockCid); err != nil {
		conn.Close()
		return nil
	}
	// Sent once per (re)connection, right after the handshake, so a host
	// restart (which wipes AgentServiceRegistry's in-memory map) gets this
	// agent's service list back without needing the VM to reboot too.
	if _, err := fmt.Fprintf(conn, "SERVICES %s\n", w.servicesJSON); err != nil {
		conn.Close()
		return nil
	}
	return conn
}

func (w *reconnectingLogWriter) Write(p []byte) (int, error) {
	w.mu.Lock()
	conn := w.conn
	w.mu.Unlock()

	if conn == nil {
		return len(p), nil // best-effort: no live connection right now, drop silently
	}
	if _, err := conn.Write(p); err != nil {
		w.mu.Lock()
		if w.conn == conn {
			w.conn = nil
		}
		w.mu.Unlock()
		// connectLoop's blocked Read on this conn will also unblock once the
		// write failure tears down the socket, triggering its own redial.
	}
	return len(p), nil
}

type identity struct {
	AgentID     string `json:"agent_id"`
	AgentName   string `json:"agent_name"`
	TenantID    string `json:"tenant_id"`
	PrimaryPort int    `json:"primary_port"`
	IngressMode string `json:"ingress_mode"`
	VsockCID    int    `json:"vsock_cid"`
}

func main() {
	mustMountInitialFS()
	switchToWritableOverlayRoot()
	cmdline := mustReadCmdline()

	// Bring eth0 up + configure IP/route from kernel cmdline. The orchestrator's
	// WaitForVmListenerAsync depends on the in-VM HTTP server being reachable
	// over L3 from the host bridge; without networking the agent never flips
	// to Running.
	gatewayIp := cmdline["agent_gw"]
	if ipCidr, ok := cmdline["agent_ip"]; ok {
		if gatewayIp != "" {
			if err := configureEthernet(ipCidr, gatewayIp); err != nil {
				fmt.Fprintf(os.Stderr, "[mvm-init] WARN configureEthernet: %v\n", err)
			}
		}
	}
	configureDNS()

	// Read identity straight from the kernel cmdline. Originally this came from
	// the host-side metadata service over vsock (FR-D08) — that's deferred
	// because configuring vsock UDS paths inside the jailer chroot needs more
	// plumbing. The cmdline carries everything in-VM init needs to start the
	// hello-world server. The vsock helpers (fetchIdentityOverVsock + sockaddrVM
	// + fdConn) are kept in this file for future revival.
	id := &identity{
		AgentID:     cmdline["agent_id"],
		AgentName:   cmdline["agent_name"],
		TenantID:    cmdline["agent_tenant"],
		PrimaryPort: mustParseInt(cmdline, "agent_port"),
		IngressMode: "gated", // default; cmdline doesn't carry it. Re-add when needed.
		VsockCID:    mustParseInt(cmdline, "agent_cid"),
	}

	logFwd = newReconnectingLogWriter(gatewayIp, id.VsockCID, loadServicesManifest())

	logLine("[mvm-init] agent=%s (id=%s tenant=%s) primary_port=%d",
		id.AgentName, id.AgentID, id.TenantID, id.PrimaryPort)

	initialSecrets, err := fetchMmdsSecrets()
	if err != nil {
		logLine("[mvm-init] WARN: mmds secrets fetch failed: %v", err)
		initialSecrets = nil
	} else if err := writeSecrets(initialSecrets); err != nil {
		logLine("[mvm-init] WARN: mmds secrets write failed: %v", err)
	} else if len(initialSecrets) > 0 {
		logLine("[mvm-init] wrote %d secret(s) to %s", len(initialSecrets), secretsDir)
	}
	startMmdsSecretsPoller(initialSecrets)
	runBootHook(id)

	startShellServer()
	startFileBrowserServer(id.AgentName)
	serveHelloWorld(id)
}

// ── filesystem bootstrap ─────────────────────────────────────────────────────

// mustMountInitialFS mounts proc/sysfs/devtmpfs/devpts/tmpfs at the bare root.
// switchToWritableOverlayRoot mounts a SECOND, independent set of these same
// filesystem types under the chroot if it succeeds — but it can also fail
// (e.g. no data disk attached) and fall back to staying on this bare root, so
// these mounts must be complete and correct on their own, not just a partial
// "good enough to set up the overlay" set. (A previous version of this code
// only mounted proc/sysfs/devtmpfs here, deferring devpts/tmpfs to the
// chroot-only path — that broke ttyd's pty allocation whenever the overlay
// setup bailed out, since devpts never got mounted anywhere in that case.)
func mustMountInitialFS() {
	for _, s := range chrootFilesystemSpecs("") {
		mountOrWarn(s.source, s.target, s.fstype, 0)
	}
}

// mountOrWarn mkdirs the target and mounts it, logging (not failing) on error —
// EBUSY (kernel already mounted it) is expected and silent.
func mountOrWarn(source, target, fstype string, flags uintptr) bool {
	_ = os.MkdirAll(target, 0755)
	if err := syscall.Mount(source, target, fstype, flags, ""); err != nil {
		if !errnoIs(err, syscall.EBUSY) {
			fmt.Printf("[mvm-init] warning: mount %s failed: %v\n", target, err)
			return false
		}
	}
	return true
}

func errnoIs(err error, target syscall.Errno) bool {
	if e, ok := err.(syscall.Errno); ok {
		return e == target
	}
	return false
}

// ── writable overlay root (FR-E03) ───────────────────────────────────────────
//
// The rootfs drive is mounted read-only by the orchestrator (is_read_only:true
// on /drives/rootfs) — every agent hard-links the same backing file, so a
// writable root would let one agent's `apk add` corrupt every other agent's
// image. Instead: format the agent's own data disk (FR-E03, pre-formatted
// ext4 by the privileged helper at disk-creation time) as the upper+work
// layer of an overlayfs whose lower is this read-only root, then chroot into
// the merged view. Everything from here on (the workload, ttyd, and any shell
// session it spawns) sees a normal writable filesystem — apk/opkg work
// exactly like on any other Alpine box — while changes live on the agent's
// own persistent disk, not the shared image.
//
// Best-effort: if any step fails (e.g. no second drive attached, or this
// kernel build lacks overlay support), we log a WARN and keep running with
// the original read-only root rather than crash-looping the agent — losing
// writable-root is much better than losing the agent entirely.
func switchToWritableOverlayRoot() {
	// /mnt, not /data: the root filesystem is mounted read-only (that's the
	// entire point of this function), so mkdir-ing a brand new mountpoint
	// directory on it fails — silently, since we're past the point of being
	// able to write anywhere to even report it usefully other than via this
	// mount failing with a confusing ENOENT on a target that was simply never
	// created. /mnt already exists in the base Alpine image, so it needs no
	// mkdir before we mount the data disk onto it.
	const dataMount = "/mnt"
	const mergedRoot = "/mnt/merged"

	dataDev, err := findDataDevice()
	if err != nil {
		fmt.Printf("[mvm-init] warning: no data disk found; root stays read-only: %v\n", err)
		return
	}

	if err := syscall.Mount(dataDev, dataMount, "ext4", 0, ""); err != nil {
		fmt.Printf("[mvm-init] warning: mount %s ext4 failed; root stays read-only: %v\n", dataDev, err)
		return
	}

	upper := dataMount + "/upper"
	work := dataMount + "/work"
	for _, d := range []string{upper, work, mergedRoot} {
		if err := os.MkdirAll(d, 0755); err != nil {
			fmt.Printf("[mvm-init] warning: mkdir %s failed; root stays read-only: %v\n", d, err)
			return
		}
	}

	overlayOpts := fmt.Sprintf("lowerdir=/,upperdir=%s,workdir=%s", upper, work)
	if err := syscall.Mount("overlay", mergedRoot, "overlay", 0, overlayOpts); err != nil {
		fmt.Printf("[mvm-init] warning: overlay mount failed; root stays read-only: %v\n", err)
		return
	}

	for _, s := range chrootFilesystemSpecs(mergedRoot) {
		mountOrWarn(s.source, s.target, s.fstype, 0)
	}

	if err := syscall.Chroot(mergedRoot); err != nil {
		fmt.Printf("[mvm-init] warning: chroot %s failed; root stays read-only: %v\n", mergedRoot, err)
		return
	}
	if err := os.Chdir("/"); err != nil {
		// Shouldn't happen post-chroot; not worth unwinding the chroot over.
		fmt.Printf("[mvm-init] warning: chdir / after chroot failed: %v\n", err)
	}
	fmt.Printf("[mvm-init] root switched to writable overlay (upper layer on %s)\n", dataDev)
}

// findDataDevice picks the virtio-blk device that ISN'T currently serving as
// root. Deliberately not hardcoded to /dev/vdb: Firecracker's actual
// vda/vdb assignment for a given drive_id is an implementation detail of API
// call order we don't want this code to depend on guessing correctly —
// comparing each candidate's device number against root's is correct
// regardless of which way the orchestrator's two PUT /drives/* calls actually
// got numbered.
func findDataDevice() (string, error) {
	var rootStat syscall.Stat_t
	if err := syscall.Stat("/", &rootStat); err != nil {
		return "", fmt.Errorf("stat /: %w", err)
	}

	for _, dev := range []string{"/dev/vda", "/dev/vdb", "/dev/vdc"} {
		var st syscall.Stat_t
		if err := syscall.Stat(dev, &st); err != nil {
			continue // device doesn't exist (e.g. only one drive attached)
		}
		if st.Rdev != rootStat.Dev {
			return dev, nil
		}
	}
	return "", fmt.Errorf("no non-root virtio-blk device found among /dev/vd{a,b,c}")
}

type mountSpec struct {
	source, target, fstype string
}

// chrootFilesystemSpecs returns the proc/sysfs/devtmpfs/devpts/tmpfs mount
// set rooted at the given prefix ("" for the bare root, or a chroot merge
// point) — shared by mustMountInitialFS (bare root) and
// switchToWritableOverlayRoot (the chroot, a second independent set of these
// same filesystem types mounted under it).
func chrootFilesystemSpecs(root string) []mountSpec {
	return []mountSpec{
		{"proc", root + "/proc", "proc"},
		{"sysfs", root + "/sys", "sysfs"},
		{"devtmpfs", root + "/dev", "devtmpfs"},
		// ttyd allocates a PTY via /dev/ptmx — devtmpfs alone creates that
		// device node, but the kernel can't expose the matching /dev/pts/N
		// slave without devpts actually mounted there.
		{"devpts", root + "/dev/pts", "devpts"},
		{"tmpfs", root + "/tmp", "tmpfs"},
		// /run/secrets (see writeSecrets below) needs to be tmpfs too — tenant
		// secrets must never land on the persistent /data disk or the shared
		// read-only rootfs image.
		{"tmpfs", root + "/run", "tmpfs"},
	}
}

// ── cmdline parsing ──────────────────────────────────────────────────────────

// mustReadCmdline parses /proc/cmdline into a map of key=value pairs.
// Bare flags (no `=`) are stored with empty value.
func mustReadCmdline() map[string]string {
	data, err := os.ReadFile("/proc/cmdline")
	if err != nil {
		fail("read /proc/cmdline: %v", err)
	}
	out := make(map[string]string)
	for _, tok := range strings.Fields(string(data)) {
		if eq := strings.Index(tok, "="); eq >= 0 {
			out[tok[:eq]] = tok[eq+1:]
		} else {
			out[tok] = ""
		}
	}
	return out
}

func mustParseInt(cmdline map[string]string, key string) int {
	v, ok := cmdline[key]
	if !ok {
		fail("%s not found on cmdline; orchestrator must pass %s=<value>", key, key)
	}
	n, err := strconv.Atoi(v)
	if err != nil {
		fail("%s=%q: %v", key, v, err)
	}
	return n
}

// configureEthernet brings up eth0 inside the guest with the given IP and
// default route. Uses ioctl directly via syscalls — busybox `ip` is not
// available in the minimal Alpine rootfs.
//
// To keep this simple and avoid a netlink library dependency, we shell out
// to `ip` via busybox if it's present, with a fallback to direct syscalls.
// In our Alpine + busybox-static rootfs, `ip` IS available via busybox's
// applet.
func configureEthernet(ipCidr, gatewayIp string) error {
	type cmd struct {
		bin  string
		args []string
	}
	steps := []cmd{
		{"/sbin/ip", []string{"link", "set", "lo", "up"}},
		{"/sbin/ip", []string{"link", "set", "eth0", "up"}},
		{"/sbin/ip", []string{"addr", "add", ipCidr, "dev", "eth0"}},
		{"/sbin/ip", []string{"route", "add", "default", "via", gatewayIp}},
	}
	for _, s := range steps {
		out, err := exec.Command(s.bin, s.args...).CombinedOutput()
		if err != nil {
			// Address-already-assigned and route-already-exists are fine on
			// reboot; everything else is a real error.
			msg := string(out)
			if strings.Contains(msg, "exists") || strings.Contains(msg, "File exists") {
				continue
			}
			return fmt.Errorf("%s %s: %v: %s", s.bin, strings.Join(s.args, " "), err, msg)
		}
	}
	fmt.Printf("[mvm-init] eth0 configured: %s gw=%s\n", ipCidr, gatewayIp)
	return nil
}

// configureDNS writes a resolv.conf with public resolvers. The platform
// doesn't run a DNS forwarder on the bridge gateway (IptablesOperations only
// sets up MASQUERADE + tenant FORWARD isolation, no dnsmasq/resolved), so
// pointing at the gateway wouldn't resolve anything — egress to the internet
// itself already works (MASQUERADE), it's just unresolved without this.
// Without it, name resolution fails silently while the connection/route
// otherwise looks fine — exactly the symptom that motivated this (apk update
// returning "temporary error" with no obviously DNS-shaped message).
func configureDNS() {
	const resolvConf = "nameserver 1.1.1.1\nnameserver 8.8.8.8\n"
	if err := os.WriteFile("/etc/resolv.conf", []byte(resolvConf), 0644); err != nil {
		fmt.Fprintf(os.Stderr, "[mvm-init] WARN configureDNS: %v\n", err)
		return
	}
	fmt.Println("[mvm-init] /etc/resolv.conf written (1.1.1.1, 8.8.8.8)")
}

// ── MMDS secrets fetch ───────────────────────────────────────────────────────
//
// Tenant-managed secrets (API keys etc., set via the Console) arrive here via
// Firecracker's native MMDS — not the vsock metadata fetch below, which was
// never wired up (see the comment in main() near where identity is read from
// cmdline instead). MMDS is per-VM by construction (this agent's guest can
// only ever reach this agent's own MMDS store), so unlike the vsock design
// there's no identity to prove or spoof here at all.
//
// Version 2: a stateful PUT mints a short-lived session token, then the GET
// must present it — mirrors EC2 IMDSv2, hardening against this agent's own
// workload leaking the token via an accidental SSRF (agents are explicitly
// meant to make arbitrary outbound calls, unlike a normal sandboxed app).
//
// Fetched once at boot AND on every startMmdsSecretsPoller tick thereafter:
// AgentSecretsService pushes a live PUT /mmds to a Running agent's own MMDS
// store the moment a tenant sets/deletes a secret from the Console, but
// nothing delivers that into the guest unless something in here re-fetches
// it — a one-shot boot-only fetch meant a secret added (or removed) after an
// agent was already running only ever took effect on the next full restart.
const (
	mmdsIP           = "169.254.169.254"
	mmdsTokenTTLSecs = 21600 // max allowed by Firecracker's MMDS v2
	secretsDir       = "/run/secrets"
	mmdsPollInterval = 10 * time.Second
)

type mmdsDocument struct {
	Secrets map[string]string `json:"secrets"`
}

func fetchMmdsSecrets() (map[string]string, error) {
	client := &http.Client{Timeout: 3 * time.Second}

	tokenReq, err := http.NewRequest(http.MethodPut, "http://"+mmdsIP+"/latest/api/token", nil)
	if err != nil {
		return nil, fmt.Errorf("build token request: %w", err)
	}
	tokenReq.Header.Set("X-metadata-token-ttl-seconds", strconv.Itoa(mmdsTokenTTLSecs))
	tokenResp, err := client.Do(tokenReq)
	if err != nil {
		return nil, fmt.Errorf("mmds token request: %w", err)
	}
	defer tokenResp.Body.Close()
	tokenBody, err := io.ReadAll(tokenResp.Body)
	if err != nil {
		return nil, fmt.Errorf("read mmds token: %w", err)
	}
	if tokenResp.StatusCode != http.StatusOK {
		return nil, fmt.Errorf("mmds token: status %d: %s", tokenResp.StatusCode, tokenBody)
	}
	token := strings.TrimSpace(string(tokenBody))

	docReq, err := http.NewRequest(http.MethodGet, "http://"+mmdsIP+"/", nil)
	if err != nil {
		return nil, fmt.Errorf("build mmds document request: %w", err)
	}
	docReq.Header.Set("X-metadata-token", token)
	docReq.Header.Set("Accept", "application/json")
	docResp, err := client.Do(docReq)
	if err != nil {
		return nil, fmt.Errorf("mmds document request: %w", err)
	}
	defer docResp.Body.Close()
	docBody, err := io.ReadAll(docResp.Body)
	if err != nil {
		return nil, fmt.Errorf("read mmds document: %w", err)
	}
	if docResp.StatusCode != http.StatusOK {
		return nil, fmt.Errorf("mmds document: status %d: %s", docResp.StatusCode, docBody)
	}

	var doc mmdsDocument
	if err := json.Unmarshal(docBody, &doc); err != nil {
		return nil, fmt.Errorf("mmds document json parse: %w; body=%s", err, docBody)
	}
	return doc.Secrets, nil
}

// writeSecrets writes each secret to its own file under secretsDir (mode
// 0600, tmpfs — see chrootFilesystemSpecs) plus a combined dotenv-style
// `env` file for convenience, and removes any previously-written secret
// file whose key is no longer present (so a secret deleted from the
// Console actually disappears from the guest, not just stops being
// refreshed). Called both once at boot and on every poller tick — a
// tenant's own long-running process can either re-source secretsDir/env
// itself or, simpler, just watch it (e.g. inotify) for changes, since this
// file is now kept current for the life of the VM rather than written once.
func writeSecrets(secrets map[string]string) error {
	if len(secrets) == 0 {
		if _, err := os.Stat(secretsDir); os.IsNotExist(err) {
			return nil // nothing written yet, nothing to reconcile
		}
	}
	if err := os.MkdirAll(secretsDir, 0700); err != nil {
		return fmt.Errorf("mkdir %s: %w", secretsDir, err)
	}

	existing, err := os.ReadDir(secretsDir)
	if err != nil {
		return fmt.Errorf("read %s: %w", secretsDir, err)
	}
	for _, entry := range existing {
		name := entry.Name()
		if name == "env" {
			continue
		}
		if _, stillPresent := secrets[name]; !stillPresent {
			if err := os.Remove(secretsDir + "/" + name); err != nil {
				return fmt.Errorf("remove stale secret %s: %w", name, err)
			}
		}
	}

	var env strings.Builder
	for key, value := range secrets {
		path := secretsDir + "/" + key
		if err := writeFileAtomic(path, []byte(value), 0600); err != nil {
			return fmt.Errorf("write %s: %w", path, err)
		}
		env.WriteString(key)
		env.WriteString("=")
		env.WriteString(value)
		env.WriteString("\n")
	}
	if err := writeFileAtomic(secretsDir+"/env", []byte(env.String()), 0600); err != nil {
		return fmt.Errorf("write %s/env: %w", secretsDir, err)
	}
	return nil
}

// writeFileAtomic writes via a temp file + rename so a process reading
// secretsDir/env (or a single key file) mid-poll-tick never observes a
// truncated/partial write — rename is atomic within the same tmpfs mount.
func writeFileAtomic(path string, data []byte, perm os.FileMode) error {
	tmp := path + ".tmp"
	if err := os.WriteFile(tmp, data, perm); err != nil {
		return err
	}
	return os.Rename(tmp, path)
}

// startMmdsSecretsPoller re-fetches the MMDS secrets document every
// mmdsPollInterval and rewrites secretsDir whenever it differs from what
// was last written, so a secret set/deleted from the Console after this
// agent already booted takes effect without a restart. Best-effort: a
// failed poll is logged and retried on the next tick, mirroring every other
// background loop in this file (log forwarder redial, ttyd reap).
func startMmdsSecretsPoller(initial map[string]string) {
	last := initial
	go func() {
		ticker := time.NewTicker(mmdsPollInterval)
		defer ticker.Stop()
		for range ticker.C {
			secrets, err := fetchMmdsSecrets()
			if err != nil {
				logLine("[mvm-init] WARN: mmds secrets poll failed: %v", err)
				continue
			}
			if secretsEqual(secrets, last) {
				continue
			}
			if err := writeSecrets(secrets); err != nil {
				logLine("[mvm-init] WARN: mmds secrets poll write failed: %v", err)
				continue
			}
			logLine("[mvm-init] mmds secrets changed: refreshed %d secret(s) in %s", len(secrets), secretsDir)
			last = secrets
		}
	}()
}

func secretsEqual(a, b map[string]string) bool {
	if len(a) != len(b) {
		return false
	}
	for k, v := range a {
		if bv, ok := b[k]; !ok || bv != v {
			return false
		}
	}
	return true
}

// ── distro boot hook (optional, distro-provided) ────────────────────────────
//
// init.go stays distro-agnostic (see file header) — it has no idea what, if
// anything, a given rootfs wants to run beyond the stock ttyd/filebrowser/
// hello-world trio below. A distro that needs extra startup behavior (e.g.
// the "hermes" distro launching an AI agent process configured from tenant
// secrets — see rootfs/distros/hermes/on-boot.sh) ships its own script at
// bootHookPath baked into its own Dockerfile; stock alpine/ubuntu simply
// don't include that file, so this is a silent no-op for them — same
// "adding a distro changes nothing else in the pipeline" property the README
// already promises for Dockerfiles.
//
// Runs after secrets are already under /run/secrets (see main()) so the hook
// can read them immediately, and in the background (like ttyd/filebrowser
// below) so a long-running foreground process the hook ultimately execs (a
// server, not a one-shot setup script) never blocks the stock startup
// sequence that follows it.
//
// Identity (AGENT_NAME/AGENT_ID/TENANT_ID) is passed via cmd.Env — generic,
// not hermes-specific, since any distro's hook may need to know which agent
// it's running as (e.g. to build the /vms/<name>/p/<port> prefix a proxy
// in front of some workload needs to tell that workload about itself, the
// same reason startFileBrowserServer's baseURL needs agentName below).
const bootHookPath = "/etc/mvm/on-boot.sh"

// servicesManifestPath — a distro optionally ships a static JSON array here
// declaring the named, ingress-reachable services it exposes beyond the
// stock shell(7681)/filebrowser(7682) ports, e.g. hermes/Dockerfile bakes
// [{"name":"hermes","label":"Hermes Dashboard","port":9119}]. Baked at image
// build time (like on-boot.sh) rather than discovered at runtime by scanning
// listening sockets: every service on these distros already runs on a
// fixed, well-known port declared in that distro's own on-boot.sh, so there
// is nothing dynamic to discover — a static manifest gives a meaningful
// label for free instead of guessing one from a process name. Read once at
// boot and reported to the host over the same reconnecting TCP connection
// already used for log forwarding (see reconnectingLogWriter.dial) — no new
// listener/port needed, and re-sent on every reconnect so a host restart
// (which wipes its in-memory registry) gets it back without needing this VM
// to reboot too.
const servicesManifestPath = "/etc/mvm/services.json"

// loadServicesManifest reads this distro's static service manifest, or
// returns "[]" (a valid empty JSON array, never nil/empty bytes) if the
// distro shipped none — stock alpine/ubuntu have no extra named services.
func loadServicesManifest() []byte {
	data, err := os.ReadFile(servicesManifestPath)
	if err != nil {
		return []byte("[]")
	}
	// Re-marshal through json.Marshal(RawMessage) rather than trusting the
	// file's bytes verbatim: this collapses it to compact form (no embedded
	// newlines, which would break the newline-delimited wire protocol below)
	// and validates it's actually well-formed JSON before it ever reaches
	// the host parser.
	var raw json.RawMessage
	if err := json.Unmarshal(data, &raw); err != nil {
		logLine("[mvm-init] WARN: %s is not valid JSON, ignoring: %v", servicesManifestPath, err)
		return []byte("[]")
	}
	compact, err := json.Marshal(raw)
	if err != nil {
		return []byte("[]")
	}
	return compact
}

func runBootHook(id *identity) {
	if _, err := os.Stat(bootHookPath); err != nil {
		return // no hook shipped by this distro — expected for alpine/ubuntu
	}
	cmd := exec.Command("/bin/sh", bootHookPath)
	cmd.Env = []string{
		"AGENT_NAME=" + id.AgentName,
		"AGENT_ID=" + id.AgentID,
		"TENANT_ID=" + id.TenantID,
	}
	cmd.Stdout = io.MultiWriter(os.Stdout, logFwd)
	cmd.Stderr = io.MultiWriter(os.Stderr, logFwd)
	if err := cmd.Start(); err != nil {
		logLine("[mvm-init] WARN: boot hook failed to start: %v", err)
		return
	}
	logLine("[mvm-init] boot hook started: %s", bootHookPath)
	go func() {
		if err := cmd.Wait(); err != nil {
			logLine("[mvm-init] WARN: boot hook exited: %v", err)
		}
	}()
}

// ── vsock metadata fetch ─────────────────────────────────────────────────────

// sockaddr_vm layout from <linux/vm_sockets.h>:
//
//	struct sockaddr_vm {
//	  unsigned short svm_family;      // AF_VSOCK
//	  unsigned short svm_reserved1;
//	  unsigned int   svm_port;
//	  unsigned int   svm_cid;
//	  unsigned char  svm_zero[4];
//	};
type sockaddrVM struct {
	family    uint16
	reserved1 uint16
	port      uint32
	cid       uint32
	_         [4]byte
}

func fetchIdentityOverVsock(myCid int) (*identity, error) {
	fd, err := syscall.Socket(AF_VSOCK, syscall.SOCK_STREAM, 0)
	if err != nil {
		return nil, fmt.Errorf("socket(AF_VSOCK): %w", err)
	}
	defer syscall.Close(fd)

	addr := sockaddrVM{
		family: AF_VSOCK,
		port:   METADATA_PORT,
		cid:    HOST_CID,
	}
	_, _, errno := syscall.Syscall(
		syscall.SYS_CONNECT,
		uintptr(fd),
		uintptr(unsafe.Pointer(&addr)),
		unsafe.Sizeof(addr),
	)
	if errno != 0 {
		return nil, fmt.Errorf("connect(vsock cid=%d port=%d): %w", HOST_CID, METADATA_PORT, errno)
	}

	// Wrap fd in a net.Conn-compatible io.ReadWriter via os.File
	// (we use Read/Write directly on the fd via small helpers).
	conn := &fdConn{fd: fd}

	req := fmt.Sprintf(
		"GET /agent/metadata HTTP/1.1\r\n"+
			"Host: localhost\r\n"+
			"Accept: application/json\r\n"+
			"X-Mvm-Vsock-Cid: %d\r\n"+
			"Connection: close\r\n"+
			"\r\n", myCid)
	if _, err := conn.Write([]byte(req)); err != nil {
		return nil, fmt.Errorf("vsock write: %w", err)
	}

	// Read until EOF.
	br := bufio.NewReader(conn)
	statusLine, err := br.ReadString('\n')
	if err != nil {
		return nil, fmt.Errorf("vsock read status: %w", err)
	}
	// Drain headers.
	for {
		line, err := br.ReadString('\n')
		if err != nil {
			return nil, fmt.Errorf("vsock read headers: %w", err)
		}
		if line == "\r\n" || line == "\n" {
			break
		}
	}

	if !strings.Contains(statusLine, "200") {
		body, _ := io.ReadAll(br)
		return nil, fmt.Errorf("metadata returned non-200: %s; body=%s", strings.TrimSpace(statusLine), string(body))
	}

	body, err := io.ReadAll(br)
	if err != nil {
		return nil, fmt.Errorf("vsock read body: %w", err)
	}

	var id identity
	if err := json.Unmarshal(body, &id); err != nil {
		return nil, fmt.Errorf("metadata json parse: %w; body=%s", err, string(body))
	}
	if id.PrimaryPort == 0 {
		return nil, fmt.Errorf("metadata response missing primary_port")
	}
	return &id, nil
}

// fdConn wraps a raw FD as io.ReadWriter — enough for our line-oriented HTTP read.
type fdConn struct{ fd int }

func (c *fdConn) Read(p []byte) (int, error)  { return syscall.Read(c.fd, p) }
func (c *fdConn) Write(p []byte) (int, error) { return syscall.Write(c.fd, p) }

// ── shell server (ttyd) ──────────────────────────────────────────────────────

// startShellServer launches ttyd in the background on :7681, giving every
// agent shell access via the platform's generic /vms/<name>/p/<port>/...
// ingress pathway (see the boot-sequence comment above for why no
// --base-path). Best-effort: a failure here must not block the actual
// workload from starting, so errors are logged, never fatal — this mirrors
// configureEthernet's WARN-and-continue style above.
func startShellServer() {
	// Absolute paths required throughout: PID 1 boots with no environment at
	// all (the kernel doesn't populate one, and we never set $PATH), so any
	// exec-by-bare-name fails its own PATH lookup. That applies twice here —
	// once for launching ttyd itself, and again for "sh", which ttyd execs
	// internally (via its own inherited, empty environment) for every client
	// connection. Both silently failed before this fix.
	cmd := exec.Command("/usr/bin/ttyd", "-p", "7681", "-W", "/bin/sh")
	cmd.Stdout = io.MultiWriter(os.Stdout, logFwd)
	cmd.Stderr = io.MultiWriter(os.Stderr, logFwd)
	if err := cmd.Start(); err != nil {
		logLine("[mvm-init] WARN: ttyd failed to start: %v", err)
		return
	}
	logLine("[mvm-init] ttyd listening on :7681 (shell access)")
	// PID 1 must reap its children — wait in the background so a crashed
	// ttyd doesn't accumulate as a zombie. No auto-restart in v1.
	go func() {
		if err := cmd.Wait(); err != nil {
			logLine("[mvm-init] WARN: ttyd exited: %v", err)
		}
	}()
}

// ── file browser (filebrowser, proxy-auth, per-user auto-provisioned) ──────
//
// Gives every agent a web file manager over its persistent disk, reachable
// via the platform's generic /vms/<name>/p/7682/... ingress pathway like
// ttyd above — no platform auth carve-out.
//
// filebrowser's own "proxy" auth method (auth.method=proxy) logs a request
// in by trusting a configurable header for the username, with NO password
// check — it's designed for exactly this "a trusted reverse proxy already
// authenticated this request" position. the ingress proxy (gated mode)
// already does that authentication and signs X-Mvm-User-Email with an
// HMAC the guest doesn't even need to verify, since the guest's network
// stack only ever sees what the ingress chose to forward — the transformer
// strips any client-supplied X-Mvm-* header first (see
// the ingress transformer.TransformRequestAsync) so this is not spoofable
// from outside the platform.
//
// The one thing filebrowser's proxy auth does NOT do is create a user from
// an unrecognized header value — the username must already exist in its own
// (local, per-VM, persisted-on-the-overlay-disk) user database, or login
// fails. Since the actual human accessing a gated agent varies per request
// and isn't known at image-build time, fileBrowserProxy below sits in front
// of the real filebrowser process (bound to 127.0.0.1 only, never reachable
// directly) and lazily provisions a user the first time it sees a new email
// on this header, before forwarding the request. That's the whole reason
// this is a Go shim and not just "run filebrowser directly" — it's what
// makes the per-(human)-user identity in the X-Mvm-User-Email header
// actually usable for the proxy-auth model filebrowser only supports for
// already-known users.
//
// Provisioning goes through filebrowser's own HTTP API (POST /api/users),
// authenticated as one fixed bootstrap admin account — NOT a second
// `filebrowser users add` CLI invocation per new user. filebrowser stores
// its user db in BoltDB, a single-writer-lock file format: once the real
// server process has the db file open, a second process (the CLI) opening
// the same file blocks and then fails with "Error: timeout". That failure
// mode is exactly what an earlier version of this shim hit on every single
// new user, since by definition the server is always already running by
// the time a real request arrives. The bootstrap admin itself IS created via
// the CLI, but only once, during the one-time `config init` step below,
// strictly before the server process ever starts — there's no lock yet to
// contend with at that point.
const (
	fileBrowserBin          = "/usr/local/bin/filebrowser"
	fileBrowserDbPath       = "/var/lib/filebrowser/filebrowser.db"
	fileBrowserPublicPort   = 7682 // reachable via /vms/<name>/p/7682/...
	fileBrowserInternalPort = 7683 // 127.0.0.1 only; never exposed directly
	fileBrowserIdentityHdr  = "X-Mvm-User-Email"
	// filebrowser requires --minimumPasswordLength (default 12) chars for
	// every user, despite the password never being checked at login time
	// under auth.method=proxy. Any fixed string of sufficient length is
	// equally fine for every user, including the bootstrap admin.
	fileBrowserPlaceholderPwd = "sc-proxy-auth-unused-placeholder"
	// Never exposed to any real person — exists purely so the shim has an
	// admin session to call POST /api/users with. Logging in as this
	// "user" only requires sending this exact string on the same
	// X-Mvm-User-Email header real users authenticate with, which only
	// this in-VM shim (loopback-only access to the real filebrowser) can do.
	fileBrowserAdminEmail = "sc-internal-admin@mvm.platform"
)

// agentName is needed only to compute --baseURL (see initArgs below) — the
// externally-visible ingress prefix for this agent's filebrowser, baked into
// filebrowser's OWN served HTML (script/asset <src> attributes) so the
// browser requests them at the right absolute URL. Without this, filebrowser
// defaults to root-absolute paths like "/static/assets/index-X.js", which
// the browser resolves against the actual page origin
// (https://<tenant>.<domain>/static/...) instead of under
// /vms/<name>/p/7682/ — a 404, since that bare path was never routed by
// the ingress proxy (which only matches the /vms/* prefix) in the
// first place. --baseURL only affects what filebrowser EMITS, not what it
// expects to receive: verified locally that its static handler answers the
// same bare path either way, which is exactly what arrives here, since
// the ingress transformer already strips the /vms/<name>/p/<port>
// prefix before forwarding — same "proxy strips, app only needs the prefix
// for generating correct links" shape ttyd relies on by deliberately using
// NO --base-path of its own (see startShellServer's comment); filebrowser
// just needs the prefix told to it explicitly instead of inferring it from
// the request at runtime.
func startFileBrowserServer(agentName string) {
	if _, err := os.Stat(fileBrowserDbPath); err != nil {
		if !os.IsNotExist(err) {
			logLine("[mvm-init] WARN: filebrowser: stat %s: %v", fileBrowserDbPath, err)
			return
		}
		if err := os.MkdirAll("/var/lib/filebrowser", 0700); err != nil {
			logLine("[mvm-init] WARN: filebrowser: mkdir db dir: %v", err)
			return
		}
		baseURL := fmt.Sprintf("/vms/%s/p/%d", agentName, fileBrowserPublicPort)
		initArgs := []string{
			"config", "init",
			"-d", fileBrowserDbPath,
			"-a", "127.0.0.1",
			"-p", strconv.Itoa(fileBrowserInternalPort),
			"-r", "/",
			"-b", baseURL,
			"--auth.method=proxy",
			"--auth.header=" + fileBrowserIdentityHdr,
		}
		if out, err := exec.Command(fileBrowserBin, initArgs...).CombinedOutput(); err != nil {
			logLine("[mvm-init] WARN: filebrowser config init failed: %v: %s", err, out)
			return
		}
		// Bootstrap admin — see the const block comment above for why this
		// runs here (before the server starts) and never again after.
		addArgs := []string{
			"users", "add", fileBrowserAdminEmail, fileBrowserPlaceholderPwd,
			"-d", fileBrowserDbPath,
			"--perm.admin",
		}
		if out, err := exec.Command(fileBrowserBin, addArgs...).CombinedOutput(); err != nil {
			logLine("[mvm-init] WARN: filebrowser bootstrap admin create failed: %v: %s", err, out)
			return
		}
		logLine("[mvm-init] filebrowser database initialized at %s", fileBrowserDbPath)
	}

	fbCmd := exec.Command(fileBrowserBin,
		"-d", fileBrowserDbPath,
		"-a", "127.0.0.1",
		"-p", strconv.Itoa(fileBrowserInternalPort))
	fbCmd.Stdout = io.MultiWriter(os.Stdout, logFwd)
	fbCmd.Stderr = io.MultiWriter(os.Stderr, logFwd)
	if err := fbCmd.Start(); err != nil {
		logLine("[mvm-init] WARN: filebrowser failed to start: %v", err)
		return
	}
	logLine("[mvm-init] filebrowser listening on 127.0.0.1:%d (internal)", fileBrowserInternalPort)
	go func() {
		if err := fbCmd.Wait(); err != nil {
			logLine("[mvm-init] WARN: filebrowser exited: %v", err)
		}
	}()

	proxy := newFileBrowserProxy()
	srv := &http.Server{
		Addr:              fmt.Sprintf("0.0.0.0:%d", fileBrowserPublicPort),
		Handler:           proxy,
		ReadHeaderTimeout: 5 * time.Second,
	}
	go func() {
		logLine("[mvm-init] filebrowser auto-provisioning proxy listening on :%d", fileBrowserPublicPort)
		if err := srv.ListenAndServe(); err != nil && err != http.ErrServerClosed {
			logLine("[mvm-init] WARN: filebrowser proxy exited: %v", err)
		}
	}()
}

// fileBrowserProxy auto-provisions a filebrowser user for each distinct
// X-Mvm-User-Email it sees, then forwards to the real (loopback-only)
// filebrowser process. provisioned caches emails already confirmed to exist
// (created this boot, or already present from a prior boot) so the common
// case is a map lookup, not an API round-trip.
type fileBrowserProxy struct {
	target  *httputil.ReverseProxy
	baseURL string
	http    *http.Client

	mu          sync.Mutex
	provisioned map[string]bool
}

func newFileBrowserProxy() *fileBrowserProxy {
	base := fmt.Sprintf("http://127.0.0.1:%d", fileBrowserInternalPort)
	target, err := url.Parse(base)
	if err != nil {
		fail("filebrowser proxy: parse internal target: %v", err) // unreachable: fixed, valid URL
	}
	return &fileBrowserProxy{
		target:      httputil.NewSingleHostReverseProxy(target),
		baseURL:     base,
		http:        &http.Client{Timeout: 5 * time.Second},
		provisioned: make(map[string]bool),
	}
}

func (p *fileBrowserProxy) ServeHTTP(w http.ResponseWriter, r *http.Request) {
	email := r.Header.Get(fileBrowserIdentityHdr)
	if email == "" {
		// No identity at all — e.g. a 'public' ingress_mode agent, which never
		// gets the signed header. filebrowser has no anonymous mode here; deny
		// rather than silently falling back to some shared identity.
		http.Error(w, "file browser requires gated ingress (no identity header present)", http.StatusForbidden)
		return
	}

	if err := p.ensureUser(email); err != nil {
		logLine("[mvm-init] WARN: filebrowser: provisioning %s: %v", email, err)
		http.Error(w, "failed to provision file browser user", http.StatusInternalServerError)
		return
	}

	p.target.ServeHTTP(w, r)
}

func (p *fileBrowserProxy) ensureUser(email string) error {
	p.mu.Lock()
	defer p.mu.Unlock()

	if p.provisioned[email] {
		return nil
	}

	adminToken, err := p.fbLogin(fileBrowserAdminEmail)
	if err != nil {
		return fmt.Errorf("admin login: %w", err)
	}

	exists, err := p.fbUserExists(adminToken, email)
	if err != nil {
		return fmt.Errorf("check existing user: %w", err)
	}
	if !exists {
		if err := p.fbCreateUser(adminToken, email); err != nil {
			// Re-check rather than trust the error: a concurrent request for
			// the same brand-new email could have created it between our
			// existence check above and this call — that's success, not a
			// failure, and trying to distinguish "lost the race" from "real
			// error" by sniffing filebrowser's response body is fragile (it
			// returns a bare "500 Internal Server Error" either way).
			stillMissing, recheckErr := p.fbUserExists(adminToken, email)
			if recheckErr != nil || stillMissing {
				return fmt.Errorf("create user: %w", err)
			}
		}
	}

	p.provisioned[email] = true
	logLine("[mvm-init] filebrowser user ready: %s", email)
	return nil
}

// fbLogin exchanges the proxy-auth identity header for a filebrowser session
// JWT, the same way a real browser session does on first load — just driven
// from here instead of a browser, and only ever as fileBrowserAdminEmail.
func (p *fileBrowserProxy) fbLogin(email string) (string, error) {
	req, err := http.NewRequest(http.MethodPost, p.baseURL+"/api/login", nil)
	if err != nil {
		return "", err
	}
	req.Header.Set(fileBrowserIdentityHdr, email)

	resp, err := p.http.Do(req)
	if err != nil {
		return "", err
	}
	defer resp.Body.Close()

	body, err := io.ReadAll(resp.Body)
	if err != nil {
		return "", err
	}
	if resp.StatusCode != http.StatusOK {
		return "", fmt.Errorf("login: status %d: %s", resp.StatusCode, body)
	}
	return string(body), nil
}

type fbUserListEntry struct {
	Username string `json:"username"`
}

func (p *fileBrowserProxy) fbUserExists(adminToken, email string) (bool, error) {
	req, err := http.NewRequest(http.MethodGet, p.baseURL+"/api/users", nil)
	if err != nil {
		return false, err
	}
	req.Header.Set("X-Auth", adminToken)

	resp, err := p.http.Do(req)
	if err != nil {
		return false, err
	}
	defer resp.Body.Close()

	if resp.StatusCode != http.StatusOK {
		body, _ := io.ReadAll(resp.Body)
		return false, fmt.Errorf("list users: status %d: %s", resp.StatusCode, body)
	}

	var users []fbUserListEntry
	if err := json.NewDecoder(resp.Body).Decode(&users); err != nil {
		return false, fmt.Errorf("decode user list: %w", err)
	}
	for _, u := range users {
		if u.Username == email {
			return true, nil
		}
	}
	return false, nil
}

type fbUserPerm struct {
	Admin    bool `json:"admin"`
	Execute  bool `json:"execute"`
	Create   bool `json:"create"`
	Rename   bool `json:"rename"`
	Modify   bool `json:"modify"`
	Delete   bool `json:"delete"`
	Share    bool `json:"share"`
	Download bool `json:"download"`
}

type fbCreateUserRequest struct {
	What string `json:"what"`
	Data struct {
		Username string     `json:"username"`
		Password string     `json:"password"`
		Perm     fbUserPerm `json:"perm"`
		Scope    string     `json:"scope"`
	} `json:"data"`
}

func (p *fileBrowserProxy) fbCreateUser(adminToken, email string) error {
	body := fbCreateUserRequest{What: "user"}
	body.Data.Username = email
	body.Data.Password = fileBrowserPlaceholderPwd
	// Real authorization already happened upstream at the ingress proxy
	// (Casbin-gated, tenant-scoped) before this email ever reached the VM —
	// admin here just means "not artificially restricted inside filebrowser
	// itself", not a second, independent authorization decision.
	body.Data.Perm = fbUserPerm{
		Admin: true, Execute: true, Create: true, Rename: true,
		Modify: true, Delete: true, Share: true, Download: true,
	}
	body.Data.Scope = "."

	payload, err := json.Marshal(body)
	if err != nil {
		return err
	}

	req, err := http.NewRequest(http.MethodPost, p.baseURL+"/api/users", strings.NewReader(string(payload)))
	if err != nil {
		return err
	}
	req.Header.Set("X-Auth", adminToken)
	req.Header.Set("Content-Type", "application/json")

	resp, err := p.http.Do(req)
	if err != nil {
		return err
	}
	defer resp.Body.Close()

	if resp.StatusCode != http.StatusCreated {
		respBody, _ := io.ReadAll(resp.Body)
		return fmt.Errorf("create user: status %d: %s", resp.StatusCode, respBody)
	}
	return nil
}

// ── hello-world HTTP server ──────────────────────────────────────────────────

func serveHelloWorld(id *identity) {
	addr := fmt.Sprintf("0.0.0.0:%d", id.PrimaryPort)
	mux := http.NewServeMux()
	mux.HandleFunc("/", func(w http.ResponseWriter, r *http.Request) {
		logLine("[hello-world] %s %s", r.Method, r.URL.Path)
		w.Header().Set("Content-Type", "text/plain; charset=utf-8")
		w.WriteHeader(http.StatusOK)
		fmt.Fprintf(w, "ready from %s\n", id.AgentName)
	})

	srv := &http.Server{
		Addr:              addr,
		Handler:           mux,
		ReadHeaderTimeout: 5 * time.Second,
	}
	logLine("[mvm-init] listening on %s", addr)

	if err := srv.ListenAndServe(); err != nil {
		fail("http server: %v", err)
	}
}

// ── failure path ─────────────────────────────────────────────────────────────

func fail(format string, a ...any) {
	logLine("[mvm-init] FATAL: "+format, a...)
	// Sleep a bit so the helper's diagnostic stderr capture has a chance to flush,
	// then exit. PID 1 exit triggers a kernel panic which the orchestrator detects
	// as the firecracker process exiting (jailer dies with the VM).
	time.Sleep(2 * time.Second)
	os.Exit(1)
}

// Keep listenForVsock alive for future expansion (currently unused but compiles
// against syscall.Accept4 — keeps the file's net import meaningful).
var _ = net.Listen
