# Design notes: multi-version backup

Engineering assessments behind the "Keep dated backup versions" feature.
These live in a repo-root NOTES.md rather than a comment block in
BackupScript.cs because they cover product-level tradeoffs (VSS vs Robocopy,
storage strategy) that span Settings, SettingsStore, the UI, and the generated
script; a comment in one file would orphan the reasoning from the other half
of the decision.

## VSS (Volume Shadow Copy) assessment - rejected

Question: is VSS a better foundation than Robocopy for versioned backups?

What VSS actually provides:

- A point-in-time, crash-consistent snapshot of a **source** volume. Its real
  value for a backup tool is reading files that are open/locked (Outlook PST,
  live databases, browser profiles) by copying from the frozen snapshot
  instead of the live file.
- Persistent shadow copies ("Previous Versions") keep differential copy-on-write
  data in the System Volume Information area **of the same volume**. They are
  not a copy on another disk; if the source disk dies, the shadow copies die
  with it.

Why it does not fit GUARD:

1. **It is not a versioned backup.** Snapshots are per-volume on the source
   side. The destination (external drive or network share, per
   `Settings.Dest`) gains nothing from VSS; you would still need Robocopy (or
   equivalent) to move data off the machine. VSS would only change *what we
   read from*, not *what we write*.
2. **Admin required.** Creating a snapshot (IVssBackupComponents,
   `vssadmin`, or WMI `Win32_ShadowCopy.Create`) needs elevation. GUARD is
   explicitly a no-admin-assumption portable app, and the generated
   `guard-backup.cmd` must run identically from a double-click and from the
   scheduled task (which is registered without highest privileges).
3. **Not standalone-script friendly.** A VSS-based run needs snapshot
   creation, exposing the snapshot device path (`\\?\GLOBALROOT\Device\...`),
   copying from it, and guaranteed cleanup even on failure. In a plain CMD
   script that means shelling to PowerShell/WMI with elevation and fragile
   error paths; a crash can leak persistent snapshots that quietly eat the
   source disk. That is the opposite of the current "one readable Robocopy
   script you can audit and double-click" model.
4. **Marginal benefit for the target data.** GUARD's defaults back up user
   document folders, where the locked-file problem Robocopy `/R:2 /W:5`
   retries cannot solve is rare. The one real gap (a constantly-open PST or
   database) is better noted in the README than solved with an elevation
   requirement for everyone.

Conclusion: VSS solves locked-file *reads*, not versioned backups; it needs
admin and is hostile to a portable, standalone, schedulable CMD script.
Robocopy stays.

## Versioned retention design

- Opt-in "Keep dated backup versions" mode, default OFF. When OFF the
  generated script is byte-for-byte identical to the non-versioned script.
- When ON, each run copies into `%DEST%\YYYY-MM-DD\<subfolder>\` instead of
  `%DEST%\<subfolder>\`. The date stamp is computed once per run via
  `powershell -Command Get-Date -Format yyyy-MM-dd` because `%date%` is
  locale-formatted and unparseable portably, and `wmic` is removed from
  current Windows 11 builds. PowerShell 5.1 is present on every supported
  Windows, so the script stays standalone.
- Same-day reruns reuse the same dated folder; Robocopy just brings it up to
  date. That is intentional (a rerun after a fixed error completes the day's
  version rather than creating a sibling).
- Mirror vs Additive applies *within* the dated folder, same as it applies to
  the destination root today. Mirror gives a faithful snapshot per date;
  Additive accumulates within a date but never deletes.
- After a run, the script prunes the oldest dated folders beyond the keep
  count (configurable, default 5, minimum 1). Safety rails on deletion:
  - Only directories **directly under** `%DEST%` are candidates.
  - A `findstr /r` whole-line match against the strict `YYYY-MM-DD` digit
    pattern filters the `dir /b /ad` listing, so a destination that also holds
    unrelated folders (or a user pointing DEST at Documents by mistake) never
    has anything else touched.
  - Today's folder is explicitly skipped as a belt-and-braces check, although
    lexicographic order (which matches chronological order for zero-padded
    ISO dates) already deletes oldest-first and keep >= 1 protects it.
  - No pruning in preview (`test`) mode, and no pruning when the run had
    errors: if today's copy may be incomplete, keeping an extra old version
    is the safer failure mode.
- The `@@PROGRESS@@` markers are unchanged: per-folder counts and names do
  not depend on the destination path shape.

## Space efficiency: hardlink seeding - rejected (verified unsafe)

The rsync `--link-dest` trick (seed the new dated folder with hardlinks to the
previous one, then sync over it, so unchanged files cost no extra space) only
works if the sync tool *replaces* a changed destination file with a new file
(new MFT record), breaking the link. rsync does copy-to-temp-then-rename, so
it is safe there.

Robocopy does not. Verified empirically on this machine (Windows 11, NTFS):

1. `robocopy src dst /MIR` with `file.txt` = "version1"
2. `mklink /H snap\file.txt dst\file.txt` (simulating the previous snapshot)
3. change the source, `robocopy src dst /MIR` again
4. `snap\file.txt` now reads the NEW content and `fsutil hardlink list` still
   shows both links: Robocopy opened the existing destination file and wrote
   the new data **in place**, through the shared inode.

So a hardlink-seeded tree synced with Robocopy silently rewrites every prior
"snapshot" of any file that changes - exactly the corruption a versioned
backup exists to prevent. Working around it (pre-deleting changed files
before the copy) would need a diff pass in the script, and hardlinks also
require the destination to be NTFS on one volume, which a network share or
exFAT external drive may not satisfy.

Decision: plain full copies per dated folder, with count-based pruning to
bound disk use. Honest cost: N versions = N times the data. The UI default of
5 keeps that visible and modest, and the pruning keeps it bounded.
