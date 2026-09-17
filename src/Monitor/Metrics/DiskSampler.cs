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

        DriveInfo[] drives;
        try { drives = DriveInfo.GetDrives(); }
        catch { return result; }

        // Collapse multiple mounts of the SAME underlying filesystem (bind mounts, snaps — e.g. a
        // systemd unit's StateDirectory surfacing "/" again at "/var/lib/private/<svc>"). Such mounts
        // report an identical total + free size to the byte, so a (total|used) signature groups them;
        // we keep one representative per signature — the SHORTEST mount path (so "/" wins over
        // "/var/lib/private/michka"). First-seen order is preserved.
        var sigIndex = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var d in drives)
        {
            try
            {
                if (!d.IsReady) continue;
                if (d.DriveType is not (DriveType.Fixed or DriveType.Removable or DriveType.Network)) continue;
                if (PseudoFs.Contains(d.DriveFormat)) continue;
                if (d.TotalSize <= 0) continue;

                long total = d.TotalSize;
                long free = d.TotalFreeSpace;
                long used = Math.Max(0, total - free);

                var disk = new DiskInfo
                {
                    Mount = d.Name,
                    TotalBytes = total,
                    UsedBytes = used,
                    Pct = total > 0 ? used * 100.0 / total : 0,
                };

                var sig = total + "|" + used;
                if (sigIndex.TryGetValue(sig, out var idx))
                {
                    // Same filesystem already seen — keep the shorter (primary) mount path.
                    if (disk.Mount.Length < result[idx].Mount.Length) result[idx] = disk;
                    continue;
                }
                sigIndex[sig] = result.Count;
                result.Add(disk);
            }
            catch
            {
                // Some mounts throw on access (permissions, stale NFS); skip them.
            }
        }

        return result;
    }
}
