# Design decisions: versioned backup

## VSS — rejected

VSS snapshots a source volume so locked/open files can be read from a frozen
point in time. It is not a versioned backup: snapshots live on the source
volume, so a disk failure takes them with it. Copying data off-machine still
requires Robocopy or equivalent, so VSS would only change what we *read from*.

Additional blockers for GUARD:
- Creating a snapshot requires elevation. GUARD is explicitly no-admin.
- A VSS-based script needs snapshot creation, device-path exposure, and
  guaranteed cleanup on failure — hostile to the "readable, auditable,
  double-clickable CMD script" model. A crash can leak persistent snapshots
  that silently consume the source disk.
- Locked-file failures are rare in the target data (user document folders);
  the one real gap (a constantly-open PST) is better called out in the README.

## Hardlink seeding — rejected (empirically unsafe with Robocopy)

The rsync `--link-dest` trick seeds a new dated folder with hardlinks to the
previous snapshot, then syncs over it so unchanged files cost no extra space.
Safe with rsync because it copy-to-temp-then-renames (new MFT record, old link
unaffected). Robocopy does not.

Verified on Windows 11 / NTFS:
1. `robocopy src dst /MIR` with `file.txt` = "version1"
2. `mklink /H snap\file.txt dst\file.txt`
3. Modify source; `robocopy src dst /MIR` again
4. `snap\file.txt` now contains the new content — Robocopy wrote in-place
   through the shared inode, silently corrupting every prior snapshot of any
   changed file.

Workarounds (pre-delete changed files before sync) require a diff pass and
assume NTFS on a single volume (network shares and exFAT externals excluded).
Decision: plain full copies per dated folder, bounded by count-based pruning.
