# Performance scope and validation

The window tracker still checks the HWND owner, current Windows session and exact
process creation timestamp on every poll. Only a successful official package path
validation is cached, with a maximum of 128 entries. A new process creation time
for the same PID requires path validation again. The slow `Process.MainModule`
module query has been replaced with `QueryFullProcessImageName` on a cache miss.

Desktop shutdown builds one PID index for each fresh process snapshot instead of
repeating linear searches for every window and selected descendant. It continues
to compare process timestamps and retains exact process handles through shutdown
and authorization checks. Duplicate snapshot PIDs fail before close requests.

The tray single-click timer stops as soon as the pending click is consumed; it no
longer wakes the UI every 60 ms after that click. Rebuilt menus dispose their old
items and submenus. A Start menu lookup now bounds both its PowerShell wait and
output read to three seconds, and a timed-out helper started by the lookup is
terminated. These changes do not shorten the configured graceful close timeout.

## Synthetic measurements

Automatic usage scheduling now runs at most once per 15 seconds, before CLI
capability and disk checks. At the unchanged 750 ms window polling interval,
deterministic tests reduce scheduling attempts from 80 to 4 per minute (95%).
Settings, profile and manual status changes reset the gate for an immediate check.
Usage disabled in settings skips these checks entirely. Migration, authorization
replacement and rollback disk work run off the UI thread; the window tracker and
authorization barriers remain in place.

The isolated harness in `benchmarks` was run in Release x64 with .NET 8.0.31 on
Windows 10.0.26200.0. Seven warmed samples of ten repetitions produced:

| Synthetic snapshot | Selected rows | Repeated searches, median | Indexed searches, median |
| --- | ---: | ---: | ---: |
| 1,000 rows | 101 | 1.357 ms | 0.029 ms |
| 10,000 rows | 501 | 23.799 ms | 0.184 ms |

Lookup outputs were identical. Building the dictionary allocated approximately
31 KB and 283 KB per respective operation; the old expressions allocated about
22 KB and 108 KB. The speed improvement trades a small bounded snapshot index
for fewer repeated scans. The executable cache required one path query across
800 polls of one unchanged, verified synthetic identity, compared with 800
previously. The harness also confirmed rejection of a reused PID with an
untrusted path.

These measurements cover synthetic lookup expressions and the production cache.
They are not end-to-end account switching, UI frame rate, application startup,
network request or native process shutdown timings. No native process enumeration,
termination, real profiles, authorization or Codex launch was performed by the
harness. Compression of the portable executable remains enabled; startup speed
with different compression settings has not been measured.
