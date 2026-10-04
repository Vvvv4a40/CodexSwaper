# Local 1.0.1 process selection fix

This local build is based on commit 92b9dc6d378c1c920b0103ecb954dcfc313ff09a. NuGet dependency versions are unchanged.

The original shutdown scan selected every process whose name contained Codex. That included the SYSTEM sandbox service but missed the installed desktop application's ChatGPT.exe. Profile switching stopped with Access denied before replacing authorization.

The patched Windows build recognizes Codex.exe and ChatGPT.exe only inside the installed OpenAI.Codex package's app directory under Program Files/WindowsApps, with the OpenAI package publisher suffix. It enumerates all verified windows, including minimized windows, and recognizes the installed desktop process even while it has no visible window. It does not identify a desktop app using a process-name substring. Unpackaged or relocated desktop installs have not been validated with this local patch.

Shutdown takes a Toolhelp snapshot restricted to the switcher's Windows session. Only verified desktop roots and their descendants are selected; the overlay and its descendants are excluded. The independent SYSTEM sandbox service, standalone CLI processes and unrelated applications are not shutdown roots. Parent/child creation times reject reused PID ancestry. Remembered parents' exit times let the scan follow surviving helpers that were created before the parent exited.

Selected processes are pinned with query/synchronization handles throughout shutdown. The optional forced fallback opens a termination handle, validates creation time and terminates selected processes individually. It never uses a global name-based kill or Kill(entireProcessTree:true) for desktop shutdown. Any failure to verify or terminate a selected process stops profile switching before authorization replacement. The desktop is checked again after legacy-state migration and immediately before the existing authorization switch.

The shutdown code does not request administrator rights or stop/reconfigure the sandbox service. Profile, login and backup algorithms remain those of the original commit. Error messages now distinguish failure before authorization was changed from an actual rollback.

Desktop relaunch now resolves the Start menu entry by the OpenAI.Codex package AppUserModelID, not the display label Codex. The installed app can therefore be shown as ChatGPT without making relaunch fail or accidentally selecting another ChatGPT package.

Build verification uses fake process trees and read-only native tests on the test host. It does not launch the switcher, send close messages to the running desktop, terminate user processes, log in, configure startup or inspect real authorization/profile files.

Process discovery is necessarily a snapshot. An application manually launched after the final check can still race the authorization operation. This patch does not guarantee race-free account switching against concurrent manual launches, and the existing program's broader authorization/backup security limitations remain. End-to-end switching with real accounts must be tested by the user.
