namespace MicroVMStudio.Core;

/// <summary>
/// Host-level configuration. Defaults are chosen so a fresh install works with
/// no configuration at all; the Host view can override anything here.
/// </summary>
public sealed class StudioOptions
{
    /// Root for all mutable host state. Created root-owned by packaging/install.sh.
    public string BaseDir { get; set; } = "/var/lib/microvm-studio";

    public string ImagesDir => Path.Combine(BaseDir, "images");
    public string DisksDir => Path.Combine(BaseDir, "disks");
    public string ChrootBaseDir => Path.Combine(BaseDir, "jails");
    public string DiagnosticsDir => Path.Combine(BaseDir, "diagnostics");
    public string KeysDir => Path.Combine(BaseDir, "keys");

    /// SQLite lives in the user's data dir, not BaseDir: it is per-user state and
    /// the app must be able to write it without the helper.
    public string DatabasePath { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "microvm-studio", "studio.db");

    public string HelperSocketPath { get; set; } = "/run/microvm-studio-helper.sock";
    public string HelperBinaryPath { get; set; } = "/usr/libexec/microvm-studio/helper";

    public string FirecrackerBinaryPath { get; set; } = "/usr/local/bin/firecracker";
    public string JailerBinaryPath { get; set; } = "/usr/local/bin/jailer";

    /// Single host bridge. ShopCare slices a /16 into a /24 per tenant; with no
    /// tenants there is exactly one network.
    public string BridgeName { get; set; } = "mvm0";
    public string BridgeCidr { get; set; } = "10.100.0.0/24";

    public int ProxyPort { get; set; } = 7000;
    public int LogCollectorPort { get; set; } = 9009;
    public int LogBufferLines { get; set; } = 5000;

    public int ReconcilerIntervalSeconds { get; set; } = 2;
    public int BootTimeoutSeconds { get; set; } = 30;

    public int VsockCidPoolStart { get; set; } = 100;
    public int VsockCidPoolEnd { get; set; } = 65535;
    public int RuntimeUidPoolStart { get; set; } = 60000;
    public int RuntimeUidPoolEnd { get; set; } = 65000;

    /// A dev tool churns VMs far faster than a SaaS platform, so addresses come
    /// back into the pool in minutes rather than ShopCare's 24 hours.
    public int IpQuarantineMinutes { get; set; } = 5;

    /// Consecutive restarts within RestartWindowSeconds before a VM is parked in
    /// CrashLoopBackoff instead of being restarted again.
    public int CrashLoopThreshold { get; set; } = 5;
    public int RestartWindowSeconds { get; set; } = 300;

    /// Where the vendored build pipeline lives, relative to the app or absolute.
    public string ImagesSourceDir { get; set; } = "images";
}
