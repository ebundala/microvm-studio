# MicroVM Studio — implementation plan

> **This folder is not part of ShopCare.** ShopCare is the *reference implementation* this design is
> lifted from. Nothing outside `microvm-studio/` is modified, and this folder is self-contained so it
> can be moved into its own repository at any time.
>
> Reference paths cited below (`app/src/Services/agents/`, `app/Helper/`, `images/`,
> `console/src/components/agents/`) are relative to the ShopCare repo root, i.e. `../` from here.

## Context

ShopCare contains a production Firecracker microVM runtime built for multi-tenant SaaS: every VM is
scoped to a tenant, gated by Keycloak + Casbin, and reachable through an Nginx-fronted ingress that
injects signed identity headers.

The mechanics underneath that — jailer/chroot lifecycle, netns + veth + TAP wiring, the reconciler
state machine, MMDS secret delivery, guest-declared service discovery, path-prefix reverse proxying
to in-guest ttyd/filebrowser — are genuinely useful to a developer running AI workloads on a laptop.
But they are inseparable from tenants, Postgres RLS, Keycloak and Nginx in the current codebase.

**MicroVM Studio** is a standalone Linux desktop app that lifts that stack out and drops the
multi-tenancy: one host, one user, no auth, no verticals. It is a developer tool for defining rootfs
images, defining VMs from them, running their lifecycle, and reaching the services inside them.

### Use cases

| # | As a developer I want to… | Delivered by |
|---|---|---|
| 1 | Define different rootfs images from a build pipeline | Vendored `images/` Makefile + Dockerfile-per-distro, driven from the UI |
| 2 | Define VMs that use those rootfs images | VM create form binding `kernel_image_id` + `rootfs_image_id` |
| 3 | Create / start / stop / restart / delete VMs | Reconciler state machine + helper RPCs |
| 4 | See workloads running inside a VM, on localhost | Guest-declared service registry + embedded reverse proxy |
| 5 | Feature parity with ShopCare's microVM stack, minus authn/authz | Everything below |

### Decisions already made

- **Elevation**: `pkexec`-launched resident helper daemon, gRPC over Unix socket. App itself never runs as root.
- **Ingress**: embedded Kestrel + YARP proxy on `127.0.0.1:7000`, ShopCare's `/<scope>/<name>/p/<port>/…` path scheme preserved so guest-side `--baseURL` logic works unchanged.
- **Terminal**: native Avalonia VT control (no WebView, no browser dependency).
- **Image builds**: vendor ShopCare's `make`-based pipeline; app shells out and streams output.
- **Parity scope**: MMDS secrets, disk orphan/adopt, egress firewall rules, and HMAC-signed identity headers all carry over.
- **Stack**: SQLite, .NET 10, Firecracker, jailer, Avalonia UI.

### Findings that change the shape of the build

Three results from feasibility research must be designed around — they are not optional refinements:

1. **`pkexec` cannot be fire-and-forget.** `pkexec.c` authorizes `getppid()` and refuses with
   *"Refusing to render service to dead parents"* if reparented to init; it also sets
   `PR_SET_PDEATHSIG`, whose "parent" is the *creating thread*. .NET retires idle threadpool threads,
   so spawning the helper from a threadpool thread gets it SIGTERM'd at a random later moment. The
   helper must be spawned from a **dedicated long-lived foreground thread**, with the app process
   alive as its direct parent. Helper lifetime == app lifetime, which is the intended behaviour
   anyway. Additionally polkit ≥0.106 requires the target to be an **absolute, root-owned path** — a
   binary under `~/.local` is rejected. Hence the one-time `sudo ./packaging/install.sh`.
2. **Do not hand-roll the VT parser.** Getting past "looks fine until you run vim/htop/tmux" (DEC
   private modes, scroll regions, alt screen, wide chars, truecolor SGR, bracketed paste, mouse
   reporting) is a multi-week detour. Use an existing Avalonia VT control and replace its PTY source
   with the ttyd WebSocket framing (~150 lines). Still a native Avalonia control, as chosen.
3. **SQLite has one writer for the whole database**, not per-row. ShopCare's
   `pg_try_advisory_xact_lock` per agent has no direct analogue; per-VM `SemaphoreSlim` is the wrong
   substitute. Use WAL + a single serialized writer channel.

---

## 1. Repository layout

```
microvm-studio/
├── PLAN.md
├── MicroVMStudio.sln
├── src/
│   ├── MicroVMStudio.Contracts/     # helper-rpc.proto + shared enums/DTOs
│   ├── MicroVMStudio.Core/          # store, reconciler, orchestrator, proxy, registries
│   ├── MicroVMStudio.App/           # Avalonia UI (net10.0, entry point)
│   └── MicroVMStudio.Helper/        # privileged gRPC daemon (net10.0, NativeAOT)
├── images/                          # vendored build pipeline (see §7)
│   ├── Makefile
│   ├── kernel/Makefile
│   └── rootfs/{Makefile,init.go,go.mod,distros/{alpine,ubuntu,hermes}/}
├── packaging/
│   ├── install.sh                   # the one sudo step
│   ├── com.microvmstudio.helper.policy
│   └── microvm-studio.desktop
└── tests/
    ├── MicroVMStudio.Core.Tests/
    └── MicroVMStudio.Integration.Tests/
```

**Prerequisite**: .NET 10 SDK (GA, LTS). The dev machine currently has only 9.0.202 — installing the
.NET 10 SDK is step zero. Avalonia 12.x targets `net10.0` by default.

---

## 2. Dropping multi-tenancy

Everything ShopCare scopes by tenant collapses to host scope.

| ShopCare | MicroVM Studio |
|---|---|
| `Agent.TenantId`, Postgres RLS, `PosUserContext`/`PosAdminContext` split | Gone. One SQLite DB, one context. |
| Per-tenant `/24` slice carved from a `/16` supernet (`IPAllocator.cs`) | One host bridge `mvm0` on a single configurable CIDR (default `10.100.0.0/24`). Per-VM IP allocated from it. |
| Bridge per tenant `br-<tenant>` | One bridge `mvm0`. `EnsureTenantBridge` → `EnsureHostBridge`. |
| Cross-tenant `FORWARD … -j DROP` iptables rule | Dropped. Egress rules become per-VM. |
| Keycloak JWT → Casbin permission check on every ingress request | Dropped. Proxy serves loopback only. |
| `X-Shopcare-Tenant-Id` header | Dropped from the signed set. |
| `TenantTier` quotas, `AgentRoleService`, `AgentPatch*`, `bootstrap-metadata` | Out of scope — all tenant/RBAC machinery. |
| Ingress path `/agents/<name>/p/<port>/` | `/vms/<name>/p/<port>/` |

What is kept **verbatim in shape** because it is host-level, not tenant-level: netns/veth/TAP
topology, jailer argv + cgroup limits, the Firecracker API configuration sequence, the reconciler
state machine, MMDS document delivery, the guest log/service handshake protocol, and disk
orphan/adopt semantics.

---

## 3. Process & privilege architecture

```
┌─ MicroVMStudio.App  (uid 1000, unprivileged) ──────────────────┐
│  Avalonia UI (main thread, Dispatcher.UIThread)                │
│  IHost:                                                        │
│    ├ Kestrel + YARP  → 127.0.0.1:7000  /vms/<n>/p/<port>/…     │
│    ├ VmLogCollector  → TCP <gateway-ip>:9009                   │
│    ├ Reconciler      → 2s tick                                 │
│    └ SqliteWriter    → single serialized writer channel        │
│  HelperSupervisor    → dedicated foreground Thread (see below) │
└────────────────────┬───────────────────────────────────────────┘
                     │ gRPC / HTTP2 over UDS, no TLS
                     │ /run/microvm-studio-helper.sock (0660, group=$USER)
┌────────────────────▼───────────────────────────────────────────┐
│ microvm-studio-helper  (root; CAP_NET_ADMIN CAP_DAC_OVERRIDE   │
│                              CAP_SYS_ADMIN CAP_KILL)           │
│   ip / iptables / mkfs / link / jailer spawn / FC API proxy    │
└────────────────────┬───────────────────────────────────────────┘
                     │ fork+exec
              jailer → firecracker (per VM, in its own netns + chroot,
                       dropped to a per-VM uid/gid)
```

### 3.1 One-time install (`packaging/install.sh`, run with `sudo`)

Mirrors `app/Helper/dev-helper.sh`, simplified. Must:
1. AOT-publish `MicroVMStudio.Helper` → install to **`/usr/libexec/microvm-studio/helper`**,
   root-owned `0755` (polkit rejects non-root-owned targets).
2. Install `com.microvmstudio.helper.policy` → `/usr/share/polkit-1/actions/`, with
   `<allow_active>auth_admin_keep</allow_active>` so an active local session gets one prompt per
   polkit timeout rather than one per launch.
3. Create `/var/lib/microvm-studio/{images,disks,jails,diagnostics,keys}`.
4. Verify `firecracker` + `jailer` are on `PATH`, cgroup v2 is mounted, and `/dev/kvm` is accessible;
   print actionable errors otherwise. Model on `HostCapabilityCheck.cs`.

Without step 2 the app still works — polkit falls back to the generic
`org.freedesktop.policykit.exec` action with a less friendly prompt.

### 3.2 `HelperSupervisor` — the elevation path

Non-negotiable implementation constraints from finding (1):

```csharp
// A dedicated, never-exiting foreground thread owns the helper process.
// NOT a threadpool thread: PR_SET_PDEATHSIG fires when the *creating thread*
// dies, and the threadpool retires idle threads.
var t = new Thread(HelperThreadMain) { IsBackground = false, Name = "helper-supervisor" };
```

- Launch: `pkexec /usr/libexec/microvm-studio/helper` with `UseShellExecute=false`, stdout/stderr
  redirected into the app's log pane (pkexec `execv`s in place; only fds 0/1/2 survive).
- Never `setsid`, never double-fork, never let the app exit while expecting the helper to live.
- Poll for the socket for 10s; the helper `chmod 0660` + `chgrp`s it to the invoking user's primary
  group itself (read from `PKEXEC_UID`).
- Reconnect loop: if the channel drops, surface a **"Helper disconnected — Reconnect"** banner. Do
  not auto-relaunch (that would silently re-prompt for a password).
- **Fallback when no polkit agent is running** (headless SSH, bare tiling WM): detect a `pkexec`
  exit code of 127 and show the exact `sudo /usr/libexec/microvm-studio/helper` command to paste,
  plus a "Connect to existing helper" button.
- Helper lifetime == app lifetime, by design. On app exit, running VMs keep running (jailer is
  reparented to init); on next launch the app **adopts** them — see §6.4.

### 3.3 Helper daemon

Kestrel bound **only** to the UDS (`ListenUnixSocket`), HTTP/2, no TLS, no TCP endpoint.
`CreateSlimBuilder` + `PublishAot=true`. Do **not** reference `Grpc.AspNetCore.Server.Reflection`
(AOT trim warning IL2075).

Port `app/Helper/Services/*` nearly as-is:

| Source file | Port as | Change |
|---|---|---|
| `Shell.cs` | `Shell.cs` | none — argv-array exec, never a shell string |
| `NetnsOperations.cs` | `NetnsOperations.cs` | `EnsureTenantBridge` → `EnsureHostBridge`; drop tenant naming |
| `IptablesOperations.cs` | `IptablesOperations.cs` | keep MASQUERADE; drop cross-tenant DROP; per-VM rules |
| `DiskOperations.cs` | `DiskOperations.cs` | none |
| `JailerOperations.cs` | `JailerOperations.cs` | none — incl. the `KillExistingFirecrackerForAgent` idempotency guard and stale-chroot cleanup, both of which fix real EBUSY/EEXIST boot failures |
| `FirecrackerApiProxy.cs` | `FirecrackerApiProxy.cs` | none |
| `ImageOperations.cs` | `ImageOperations.cs` | none |
| `HostNetworkingInitializer.cs` | `HostNetworkingInitializer.cs` | single bridge; keep the `ip route get` egress-interface detection |

### 3.4 RPC surface (`MicroVMStudio.Contracts/protos/helper-rpc.proto`)

Derived from `app/Helper/protos/helper-rpc.proto` (19 RPCs) with tenant fields removed:

```
EnsureHostBridge / DestroyHostBridge
CreateVmNetns / DestroyVmNetns            → returns host_veth_name, netns_veth_name, tap_name
ApplyVmEgressRules                        → full rebuild per call, declarative
CreateDiskImage / LinkDiskIntoChroot / DeleteDiskImage / ListDiskFiles
LaunchJailer / KillJailer / ReadJailerStatus / DeleteJailerChroot
FirecrackerApiCall                        → method, path, body, timeout_ms → status, body, error_code
WriteImageFile (client-streaming) / DeleteImageFile
ListHostVeths / DeleteHostVeth / ListJailerChroots   → leak detection
Ping
```

Every RPC carries `trace_id` and is idempotent in effect.

---

## 4. Data model (SQLite, EF Core)

Connection string: `Data Source=…;Journal Mode=WAL;Synchronous=Normal;Busy Timeout=5000;Pooling=True`.
**Never `Cache=Shared`** — incompatible with WAL.

```
Vm            id, name(unique), status, desired_status, firecracker_pid, netns_name,
              private_ip, vsock_cid, disk_id, vcpu, memory_mib, kernel_image_id,
              rootfs_image_id, primary_port, restart_policy, runtime_uid, runtime_gid,
              restart_count, last_restart_at, purge_disk_on_delete, created_at,
              updated_at, last_state_change_at

VmDisk        id, vm_id(nullable), path_on_host, format(raw|qcow2), size_gib,
              state(in_use|orphaned|deleting), created_at, last_attached_at

VmImage       id, kind(kernel|rootfs), name, digest(unique), path_on_host,
              description, distro_dir, size_bytes, built_at

VmSecret      id, vm_id, key, value_encrypted, created_at, updated_at

VmEgressRule  id, vm_id, cidr, effect(allow|deny), priority, created_at

VmEvent       id, vm_id, event_type, prev_status, new_status, payload_json, occurred_at

IpAllocation  id, vm_id(unique), private_ip(unique), quarantine_until, created_at

HostSetting   key, value          # bridge cidr, proxy port, log collector port, paths
```

Enums copied from `app/src/Models/agents/Agent.cs`:
- `VmStatus`: `Pending, Provisioning, Booting, Running, Stopping, Stopped, Crashed, CrashLoopBackoff, BootFailed`
- `VmDesiredStatus`: `Running, Stopped, Deleted`
- `RestartPolicy`: `Never, OnFailure, Always`

**Concurrency** (finding 3): all writes go through a `Channel<Func<AppDbContext, Task>>` consumed by
one background writer task. `ISqliteWriter.EnqueueAsync(...)`. Reads use short-lived scoped contexts
and are unrestricted (WAL readers never block). Read-then-write transactions must use
`BEGIN IMMEDIATE`. Retry `SQLITE_BUSY_SNAPSHOT` (517) by **rolling back and retrying the whole
transaction**, not the statement. Note EF Core's `CommandTimeout` does *not* set
`sqlite3_busy_timeout` (efcore#28135) — set it in the connection string.

Secrets are encrypted at rest with a key file under `/var/lib/microvm-studio/keys/` (mode `0600`,
generated on first run) — replacing ShopCare's ASP.NET Data Protection ring.

---

## 5. Networking

Single bridge instead of ShopCare's per-tenant slicing, same per-VM topology:

```
host: mvm0 (10.100.0.1/24)  ──┬── vh-<id8>  ⇄  ns-<id8> ── br-int ── tap0
                              │        (host)      (inside netns mvm-<id8>)      │
                              │                                          firecracker virtio-net
                              └── … other VMs                                    │
                                                                            guest eth0
                                                                        10.100.0.42/24
                                                                        default via 10.100.0.1
```

Exact `ip` sequence, ported from `NetnsOperations.cs`:
```
ip link add mvm0 type bridge ; ip addr add 10.100.0.1/24 dev mvm0 ; ip link set mvm0 up
ip netns add mvm-<id8>
ip link add vh-<id8> type veth peer name ns-<id8> netns mvm-<id8>
ip link set vh-<id8> master mvm0 ; ip link set vh-<id8> up
ip -n mvm-<id8> link add br-int type bridge ; …set up
ip -n mvm-<id8> link add tap0 type tun user <runtime_uid> group <runtime_gid>
ip -n mvm-<id8> link set tap0 master br-int ; …set up
ip -n mvm-<id8> link set ns-<id8> master br-int ; …set up ; …lo up
```

A dedicated TAP is required because Firecracker opens `host_dev_name` via `TUNSETIFF/IFF_TAP`, which
a veth cannot satisfy.

- Guest IP/gateway are **not** DHCP — they are passed on the kernel cmdline (`agent_ip`, `agent_gw`)
  and applied by `init.go`.
- `iptables -t nat -A POSTROUTING -s 10.100.0.0/24 -o <egress-if> -j MASQUERADE`, installed once at
  helper startup; egress interface auto-detected via `ip route get 1.1.1.1`.
- Per-VM egress rules rebuilt declaratively in a dedicated chain on every `ApplyVmEgressRules`.
- IP quarantine on delete: keep the `IpAllocation` row with `quarantine_until = now + 5min` (24h in
  ShopCare; a dev tool churns VMs far faster).

---

## 6. VM lifecycle

### 6.1 State machine

Ported from `OrchestratorService.cs` / `OrchestratorReconciler.cs`. UI actions **only** set
`desired_status`; the reconciler is the sole driver of `status`.

```
                  ┌──────────────── user: start ────────────────┐
                  ▼                                             │
create → Pending → Provisioning → Booting → Running ─ stop → Stopping → Stopped
                          │           │        │                          │
                          └─ fail ────┴→ BootFailed                    delete
                                              │                           │
                            jailer exited ──→ Crashed ──(policy)──→ CrashLoopBackoff
                                                                          ▼
                                                                    (row removed)
```

### 6.2 Boot sequence (`VmOrchestrator.ProvisionAndBootAsync`)

1. Allocate `private_ip`, `vsock_cid` (100–65535), `runtime_uid/gid` (60000–65000) — ports of
   `IPAllocator`, `VsockCidAllocator`, `RuntimeUidAllocator`.
2. `EnsureHostBridge` → `CreateVmNetns` → returns `tap_name`.
3. `CreateDiskImage` if the VM has no disk yet.
4. `LaunchJailer` → pid + `chroot_root_path`.
5. `LinkDiskIntoChroot` ×3 — kernel → `/vmlinux.bin`, rootfs → `/rootfs.img`, data disk →
   `/data.img`. Kernel and rootfs are **hard-linked** (shared inode across all VMs), which is why the
   rootfs drive must be `is_read_only: true`.
6. Firecracker API sequence via `FirecrackerApiCall`, in this exact order (order determines
   `/dev/vda` vs `/dev/vdb`):
   ```
   PUT /machine-config          {vcpu_count, mem_size_mib, track_dirty_pages:false}
   PUT /boot-source             {kernel_image_path:"/vmlinux.bin", boot_args:
                                  "console=ttyS0 reboot=k panic=1 pci=off nomodules
                                   agent_cid=… agent_ip=<cidr> agent_gw=… agent_id=…
                                   agent_name=… agent_port=…"}
   PUT /drives/rootfs           {drive_id:"rootfs", path_on_host:"/rootfs.img",
                                 is_root_device:true, is_read_only:true}
   PUT /drives/data             {drive_id:"data", path_on_host:"/data.img",
                                 is_root_device:false, is_read_only:false}
   PUT /network-interfaces/eth0 {iface_id:"eth0", host_dev_name:<tap>, guest_mac:<sha256(id)>}
   PUT /mmds/config             {version:"V2", network_interfaces:["eth0"]}
   PUT /mmds                    {vm_id, vm_name, secrets:{…}, vm_secret_b64, auth_version}
   PUT /actions                 {action_type:"InstanceStart"}
   ```
   `guest_mac` is `02:` + first 5 bytes of `sha256(vm_id)` — deterministic across restarts.
   `agent_tenant` is dropped from the cmdline.
7. Readiness probe: TCP connect to `private_ip:primary_port`, 250ms retry, 30s deadline
   (`WaitForVmListenerAsync`).
8. → `Running`, register in the proxy routing table.

Failures transition to `BootFailed` with `{code, detail}` in `VmEvent.payload_json`. Jailer stderr is
captured to `/var/lib/microvm-studio/diagnostics/<vm-id>-jailer.log` and shown in the UI on failure —
this is the single most useful debugging artifact.

### 6.3 Reconciler

`BackgroundService`, 2s tick, 3s startup delay. Per tick:
- `desired=Running` and status ∈ {Pending, Provisioning, Booting} → `ProvisionAndBootAsync`
- `status=Stopping` and `desired≠Deleted` → `TearDownAndStopAsync`
- `desired=Deleted` → `TearDownAndDeleteAsync`
- `status=Running` → `ReadJailerStatus(pid)`; if dead → `Crashed`, then apply `restart_policy`
  (`Always`/`OnFailure` → back to `Pending`, with exponential backoff into `CrashLoopBackoff` after N
  restarts in a window)

Mutual exclusion: an in-process `ConcurrentDictionary<string, SemaphoreSlim>` keyed by VM id guards
*orchestration* (not DB writes — those go through the writer channel). Single-process app, so no
cross-process lock is needed.

### 6.4 Adoption on startup

ShopCare rebuilds `IngressRoutingTable` from `Running` rows at boot. MicroVM Studio must go further,
because the app can be closed while VMs keep running: for every `Running` row, call
`ReadJailerStatus(pid)` — alive → repopulate the routing table; dead → transition to `Crashed`. Then
call `ListHostVeths` + `ListJailerChroots` and diff against the DB to surface **orphaned host
resources** in a "Host cleanup" panel with a Reclaim button.

### 6.5 Delete

`purge_disk = true` → `DeleteDiskImage` + remove row. `false` → disk becomes `orphaned` and is
offered in the create form's "adopt existing disk" dropdown. Hard delete of the `Vm` row either way;
`VmEvent` rows cascade.

---

## 7. Image build pipeline (use case 1)

Vendor `images/` from ShopCare — `Makefile`, `kernel/Makefile`,
`rootfs/{Makefile,init.go,go.mod,distros/*}`.

Pipeline per distro, unchanged:
```
docker build distros/<name>/Dockerfile
  → docker create + docker export | tar -x -C build/rootfs-staging-<name>
  → ln -sf /usr/local/bin/mvm-init  <staging>/sbin/init     # init installed post-export;
                                                            # BuildKit would exec it per RUN
  → mkfs.ext4 -F -L mvm-rootfs -d <staging> build/rootfs-<name>.ext4
      size = du -sm <staging> + 100MiB headroom, or SIZE_MB override
```
Kernel is downloaded prebuilt, not compiled:
`https://s3.amazonaws.com/spec.ccfc.min/firecracker-ci/v1.11/<arch>/vmlinux-5.10.225`.

**UI integration** (`ImagesView`):
- Distro discovery = `ls images/rootfs/distros/*/Dockerfile`. Dropping a new folder in makes it
  appear — that is use case 1's extension point.
- **Build** runs `make rootfs DISTRO=<name>` as the **unprivileged user** (docker group), streaming
  stdout/stderr to a build-log pane.
- On success: `sha256sum` the artifact, `WriteImageFile` it into
  `/var/lib/microvm-studio/images/rootfs-<name>-<sha8>.ext4` via the helper, insert a `VmImage` row.
  Replaces ShopCare's `install.sh` + `seed.sql`.
- **Import** registers an existing `.ext4`/`vmlinux` without building.
- Delete is blocked while any VM references the image.

### Guest-side changes to `init.go`

Vendored, so rename ShopCare identifiers. All in `images/rootfs/init.go`:

| Change | Detail |
|---|---|
| filebrowser `--baseURL` | `/agents/%s/p/%d` → `/vms/%s/p/%d` (must match the proxy's path scheme) |
| Identity header const | `X-Shopcare-User-Email` → `X-Mvm-User-Email` |
| MMDS field names | `agent_secret_b64` → `vm_secret_b64`, etc. |
| Log collector | keep: dial `<gateway>:9009`, send `AGENT <cid>\n`, then `SERVICES <json>\n`, then `<ts>\t<line>\n` |
| Everything else | unchanged — overlayfs mount, MMDS 10s poller, ttyd on 7681, filebrowser on 7682 |

Kernel cmdline keys stay `agent_*` (guest-internal only) to minimise the diff against upstream.

---

## 8. Service discovery + ingress (use case 4)

**Discovery is guest-declared, not port-scanned.** `init.go` reads `/etc/shopcare/services.json`
(rename to `/etc/mvm/services.json`) — baked into the image, e.g. hermes ships
`[{"name":"hermes","label":"Hermes Agent","port":9119}]` — and sends it as the `SERVICES` handshake
line on the log connection.

- `VmLogCollector` (port of `AgentLogCollectorService.cs`): TCP listener on the bridge gateway
  IP:9009. Parses the `AGENT`/`SERVICES` handshake, then streams lines into `VmLogHub` (ring buffer,
  5000 lines/VM).
- `VmServiceRegistry`: in-memory `vm_id → [{name,label,port}]`, populated from the handshake, lost on
  app restart (repopulated on the guest's next redial — `init.go` redials with backoff).
- Stock services (ttyd 7681, filebrowser 7682, primary port) are always shown, even before the
  handshake lands.

**Embedded proxy** (port of `AgentsIngressMiddleware.cs` + its transformer), Kestrel + YARP
`IHttpForwarder` on `127.0.0.1:7000`:

```
GET  http://127.0.0.1:7000/vms/dev1/p/8080/            → http://10.100.0.42:8080/
GET  http://127.0.0.1:7000/vms/dev1/p/7682/            → filebrowser (needs identity hdr)
WS   ws://127.0.0.1:7000/vms/dev1/p/7681/ws            → ttyd
```

Path regex: `^/vms/(?<name>[a-z0-9-]{1,32})/p/(?<port>\d{1,5})(?<rest>/.*)?$`. The transformer strips
the `/vms/<name>/p/<port>` prefix before forwarding — the guest apps see bare paths, which is exactly
why ttyd runs with no `--base-path` and filebrowser needs `--baseURL` told to it explicitly. YARP
proxies WebSocket upgrades transparently.

**Identity headers** (parity item): strip any client-supplied `X-Mvm-*`, then inject `X-Mvm-User-Id`,
`X-Mvm-User-Email`, `X-Mvm-Trace-Id`, `X-Mvm-Auth-Expires`, `X-Mvm-Auth-Sig`, `X-Mvm-Auth-Version`.
Identity is the local desktop user (`Environment.UserName` + a stable synthetic email). Signing uses
a port of `PlatformAuthSigner.cs`: `vm_secret = HKDF-SHA256(master_secret, sha256(vm_id))`, HMAC over
the canonical header string. The guest's filebrowser proxy **hard-requires** the email header to
provision a user, so this is load-bearing, not decorative.

Bind loopback-only. No auth on the proxy itself — single-user dev tool.

---

## 9. Avalonia UI

MVVM, `CommunityToolkit.Mvvm`. Left nav → **VMs · Images · Disks · Host**.

| View | Content |
|---|---|
| `VmListView` | Table: name, status pill, ip, vcpu/mem, uptime, rootfs image. Inline Start/Stop/Restart/Delete. Live via reconciler events pushed through `Dispatcher.UIThread`. |
| `VmCreateDialog` | name (`[a-z0-9-]{1,32}`), kernel image, rootfs image, vcpu, memory_mib, primary_port, disk (new size **or adopt orphaned**), restart_policy |
| `VmDetailView` | Tabs: **Overview** (state, ip, pid, netns, vsock cid, event timeline) · **Services** · **Shell** · **Logs** · **Secrets** · **Egress** |
| ↳ Services | Discovered + stock services, each an "Open" button → `xdg-open http://127.0.0.1:7000/vms/<n>/p/<port>/`, plus a copyable URL |
| ↳ Shell | Terminal control, §9.1 |
| ↳ Logs | Live tail from `VmLogHub` + jailer diagnostics file on failure; filter, pause, copy |
| ↳ Secrets | Key (`^[A-Z_][A-Z0-9_]{0,63}$`) / value. Write-only display. Saving to a `Running` VM re-pushes MMDS immediately (port `RepushMmdsAsync`) so `init.go`'s 10s poller picks it up without a restart |
| ↳ Egress | CIDR allow/deny rows, priority; Apply → `ApplyVmEgressRules` |
| `ImagesView` | Distro cards, Build (streamed log), Import, digest, size, "used by N VMs" |
| `DisksView` | All disks incl. orphaned; delete orphans; port of `ListDiskFiles` diff to flag files with no DB row |
| `HostView` | Helper status + Reconnect, bridge CIDR, proxy port, paths, firecracker/jailer/kvm/cgroup-v2 preflight, **Host cleanup** (orphaned veths/chroots from §6.4) |

### 9.1 Terminal control

Per finding (2): use an existing Avalonia VT control rather than writing a parser. Evaluate
`Iciclecreek.Avalonia.Terminal` (XTerm.NET-based) and `AvaloniaTerminal` (XtermSharp-based, has
selection/search) — **verify Avalonia 12 / net10.0 compatibility on NuGet before committing**; if
neither is compatible, vendor the one with the cleaner rendering layer. Avoid `VtNetCore` and
`AvalonStudio.TerminalEmulator` (dormant since ~2018).

Replace the library's PTY source with a `TtydWebSocketSource` implementing ttyd 1.7.7's framing (from
`AgentShellTerminal.vue`):

```
connect ws://127.0.0.1:7000/vms/<name>/p/7681/ws
send    {"AuthToken":"","columns":N,"rows":N}     ← bare JSON, no opcode byte
send    '0' + input bytes            |  '1' + {"columns":N,"rows":N}
recv    '0' + output bytes  |  '1' + title  |  '2' + prefs (ignore)
```
Resize on control size change. No auth token — ttyd runs without `--credential` and the proxy is
loopback-only.

### 9.2 Hosting composition

One `IHost`. `WebApplication` built and `StartAsync()`ed, **then** Avalonia started on the main
thread; wire `IHostApplicationLifetime` ↔ `IClassicDesktopStyleApplicationLifetime.Exit` in both
directions. Do not use the fire-and-forget `RunAsync()` pattern — it swallows Kestrel bind failures
(port 7000 already in use must surface as a visible error). Everything crossing into view models goes
through `Dispatcher.UIThread`. Avalonia itself is **not** NativeAOT-published; only the helper is.

---

## 10. Build order

1. **Scaffold** — solution, four projects, `.proto`, EF model + migration, `packaging/install.sh` +
   polkit policy.
2. **Helper** — port `Shell`/`Netns`/`Iptables`/`Disk`/`Jailer`/`FirecrackerApiProxy`/`Image`
   operations. Verify standalone with a `grpcurl`-over-UDS smoke script before any UI exists.
3. **Elevation** — `HelperSupervisor` on its dedicated thread, socket wait, reconnect banner, sudo
   fallback. **Prove the helper survives 10 minutes of app idle** — that is the PDEATHSIG regression
   test.
4. **Images** — vendor `images/`, patch `init.go` renames, build alpine, register it.
   `make rootfs DISTRO=alpine` must succeed from the CLI before wiring the UI.
5. **Orchestrator + reconciler** — full boot path to `Running` with a TCP-probe pass. Milestone: a VM
   boots and answers on its primary port, verified with `curl` against the guest IP.
6. **Proxy + log collector + service registry** — `/vms/<n>/p/<port>/` reachable; `SERVICES`
   handshake populating the registry.
7. **UI** — list/create/detail/lifecycle, then Services and Logs tabs.
8. **Terminal** — library evaluation, ttyd WebSocket source, resize.
9. **Parity items** — MMDS secrets (+ live re-push), disk orphan/adopt, egress rules, HMAC identity
   headers.
10. **Robustness** — adoption on startup, crash detection + restart policy, orphan sweeper, Host
    cleanup panel.

---

## 11. Verification

Each step is a real command or observable behaviour, not a unit test.

**Preflight**
```bash
dotnet --list-sdks                      # expect a 10.x entry
firecracker --version && jailer --version
ls -l /dev/kvm && stat -fc %T /sys/fs/cgroup   # expect cgroup2fs
```

**Helper**
```bash
sudo ./packaging/install.sh
pkexec /usr/libexec/microvm-studio/helper &     # expect a polkit password dialog
ls -l /run/microvm-studio-helper.sock           # expect srw-rw---- root:<your-group>
grpcurl -plaintext -unix /run/microvm-studio-helper.sock \
        mvm.helper.v1.VmHelper/Ping
```
Then from the app: launch, confirm Host view shows *Helper connected*, leave it idle 10 minutes,
confirm still connected (PDEATHSIG regression).

**Images**
```bash
cd microvm-studio/images && make kernel && make rootfs DISTRO=alpine
ls -lh build/vmlinux.bin build/rootfs-alpine.ext4
```
Then in-app: Images → Build alpine → log streams → row appears with a digest.

**Lifecycle (use case 3)**
Create `dev1` (1 vcpu, 512 MiB, 5 GiB, port 8080, alpine) → watch
`Pending → Provisioning → Booting → Running` in the list. Then on the host:
```bash
sudo ip netns list | grep mvm-           # netns exists
ip -br addr show mvm0                    # bridge up with gateway ip
pgrep -af 'jailer|firecracker'           # one jailer per running VM
curl http://10.100.0.2:8080/             # guest primary port answers
```
Stop → `Stopped`, jailer gone, netns gone. Restart → back to `Running` on the **same IP**. Delete
with *keep disk* → row gone, disk visible as orphaned in Disks. Create a new VM adopting that disk →
prior `/data` contents survive.

**Services + proxy (use case 4)**
Detail → Services lists ttyd 7681, filebrowser 7682, primary 8080.
```bash
curl -sI http://127.0.0.1:7000/vms/dev1/p/8080/    # 200
xdg-open http://127.0.0.1:7000/vms/dev1/p/7682/    # filebrowser loads, assets 200 not 404
```
The filebrowser asset check is the real test of the `--baseURL` ↔ prefix-strip contract — if
`--baseURL` and the proxy path disagree, static assets 404 while the page shell still renders.

**Shell** — Shell tab connects; run `vim`, `htop`, resize the window mid-session, confirm redraw and
no corruption.

**Logs** — Logs tab shows `[mvm-init]` boot lines; `echo hi > /dev/console` in the shell appears
live. Kill firecracker externally (`sudo kill <pid>`) → status flips to `Crashed`, then restarts if
policy is `Always`.

**Secrets** — set `OPENROUTER_API_KEY` on a running VM; in the Shell within ~10s:
`cat /run/secrets/OPENROUTER_API_KEY`. Build the hermes image and confirm the agent starts with the
key.

**Egress** — add `deny 0.0.0.0/0`, Apply, then in the Shell `ping -c1 1.1.1.1` fails while
`ping -c1 <gateway>` succeeds. Remove → egress restored.

**Adoption** — with `dev1` Running, quit the app. `pgrep firecracker` still shows it. Relaunch →
`dev1` shows `Running`, proxy works again without a reboot.

**Leak check** — after deleting every VM:
```bash
ip -br link show type veth               # no vh-* left
sudo ip netns list                       # no mvm-* left
sudo ls /var/lib/microvm-studio/jails/firecracker/   # empty
```
Host view's cleanup panel should report zero orphans.

---

## 12. Explicitly out of scope

Tenants, Keycloak, Casbin, RLS, tier quotas, agent roles, patch management, `bootstrap-metadata`,
Nginx, snapshots, per-VM subdomains, domain-based (as opposed to CIDR) egress rules, remote/multi-host
operation, Windows/macOS.
