namespace Monitor.Metrics;

/// <summary>
/// Cross-platform disk-usage sampler over <see cref="DriveInfo"/>. Filters out pseudo
/// filesystems (tmpfs, overlay, etc.) and unusable mounts so the dashboard shows real storage.
/// </summary>
public sealed class DiskSampler
{
    private static readonly HashSet<string> PseudoFs = new(StringComparer.OrdinalIgnoreCase)
    {
        "tmpfs", "devtmpfs", "squashfs", "overlay", "proc", "sysfs", "cgroup", "cgroup2",
        "devpts", "mqueue", "debugfs", "tracefs", "ramfs", "autofs", "binfmt_misc", "fuse.gvfsd-fuse",
        "configfs", "pstore", "securityfs", "hugetlbfs", "fusectl", "efivarfs", "bpf", "nsfs",
    };

    public List<DiskInfo> Sample()
    {
        var result = new List<DiskInfo>();
        // De-dupe mounts that resolve to the same total size/name (bind mounts, snaps).
        var seen = new HashSet<string>(StringComparer.Ordinal);

        DriveInfo[] drives;
        try { drives = DriveInfo.GetDrives(); }
        catch { return result; }

        foreach (var d in drives)
        {
            try
            {
                if (!d.IsReady) continue;
                if (d.DriveType is not (DriveType.Fixed or DriveType.Removable or DriveType.Network)) continue;
                if (PseudoFs.Contains(d.DriveFormat)) continue;
                if (d.TotalSize <= 0) continue;

                var mount = d.Name;
                if (!seen.Add(mount)) continue;

                long total = d.TotalSize;
                long free = d.TotalFreeSpace;
                long used = Math.Max(0, total - free);

                result.Add(new DiskInfo
                {
                    Mount = mount,
                    TotalBytes = total,
                    UsedBytes = used,
                    Pct = total > 0 ? used * 100.0 / total : 0,
                });
            }
            catch
            {
                // Some mounts throw on access (permissions, stale NFS); skip them.
            }
        }

        return result;
    }
}
