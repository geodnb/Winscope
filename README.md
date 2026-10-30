# WinScope

**See what your Windows PC has really been doing: performance history, network by app, a plain-English event log and safe storage cleanup, in one app.**

<p>
  <a href="https://github.com/geodnb/Winscope/releases/latest/download/WinScope.exe"><img alt="Download WinScope.exe" src="https://img.shields.io/badge/Download-WinScope.exe-3DD6C6?style=for-the-badge&logo=windows&logoColor=white"></a>
  <a href="https://github.com/geodnb/Winscope/releases/latest"><img alt="Latest release" src="https://img.shields.io/github/v/release/geodnb/Winscope?style=for-the-badge&label=Latest&color=2A313C"></a>
  <a href="LICENSE.md"><img alt="Licence: PolyForm Noncommercial" src="https://img.shields.io/badge/Licence-PolyForm%20Noncommercial-2A313C?style=for-the-badge"></a>
</p>

> **[⬇ Download WinScope.exe](https://github.com/geodnb/Winscope/releases/latest/download/WinScope.exe)**: a single portable file with no installer. It runs without admin rights. &nbsp;·&nbsp; [All releases](https://github.com/geodnb/Winscope/releases)

Windows' built-in tools each answer part of the question and none of them remember anything. Task Manager only shows the present, Resource Monitor is cluttered, Event Viewer is unreadable, and Disk Cleanup hides what it's about to delete. WinScope brings an improved version of each into one dark, fast app with a dashboard that shows all four at a glance.

It's a sister project to [AirScope](https://github.com/geodnb/AirScope), the Wi-Fi analyser.

<img width="2544" height="1421" alt="image" src="https://github.com/user-attachments/assets/67f7999a-bfa5-4ce0-a75f-9ff235f33103" />

## Highlights

- **Performance history**: CPU, GPU, RAM and disk for the last 2 hours, with the busiest apps recorded every second. Hover over any moment to see what was running, and look back at a spike log that names the culprit.
- **Network by app**: live download and upload speed, which apps are connected and to where, and per-app speeds when running as administrator.
- **Events in plain English**: blue screens, crashes, graphics driver resets, hardware errors, Wi-Fi drops, failed updates and unexpected shutdowns, each explained with what to try. Known-harmless noise is hidden.
- **Storage and safe cleanup**: what's filling your drives, the largest files, and cleanup that shows exactly what will be removed before anything is deleted. Your own files only ever go to the Recycle Bin.
- **Dashboard**: all four tools at a glance. Click any panel to open the full tool.
- **One-click reports**: a plain-text snapshot of what your PC is doing, made to paste into an AI assistant or send to someone helping you. Personal details are removed by default.
- **Runs as a normal user.** The few features that need admin rights offer to restart elevated, and nothing else changes.

---

## Contents

- [Highlights](#highlights)
- [The dashboard](#the-dashboard)
- [Performance](#performance)
- [Network](#network)
- [Events](#events)
- [Storage](#storage)
- [Reports](#reports)
- [Administrator features](#administrator-features)
- [Getting started](#getting-started)
- [Files WinScope creates](#files-winscope-creates)
- [Accuracy and limitations](#accuracy-and-limitations)
- [Privacy and safety](#privacy-and-safety)
- [Building from source](#building-from-source)
- [Disclaimer](#disclaimer)

---

## The dashboard

The Home screen shows four live panels:

| Panel | At a glance |
|---|---|
| **Performance** | CPU, GPU and RAM now, a 2-minute graph, the busiest app and the last spike |
| **Network** | Download and upload speed now, and the top apps |
| **Events** | How many problems Windows has logged in the last 24 hours, the latest ones, and when the PC last started |
| **Storage** | How full each drive is, how much can be cleaned safely, and a button to review it |

Click a panel, or use the navigation at the top, to open that tool. Alt+Home returns to the dashboard.

Every tool keeps collecting in the background, so the history is already there when you open it.

---

## Performance

Task Manager tells you what's busy *now*. WinScope remembers, so you can answer "what was hogging my PC when my game stuttered five minutes ago?"

- **Four graphs** (CPU, GPU, RAM, disk) over the last 5 minutes, 15 minutes, 1 hour or 2 hours. They scroll smoothly and stay detailed when zoomed out. Scroll the mouse wheel to go back in time.
- **Hover** over any moment to see the busiest apps at that second. **Click** to pin the moment: the side panel then lists the top apps by CPU, GPU, RAM and disk/IO at that time.
- **Spikes** are recorded automatically, each with the app behind it. Click one to jump to it.
  - Sustained high CPU
  - Memory running out
  - The disk maxed out
  - An app suddenly jumping in CPU use
  - A busy GPU isn't treated as a problem, because games keep it at 99%.
- **Right now** table: every app, with its process count, CPU, GPU, RAM and IO, sortable.
- **GPU per app**: uses the same counters as Task Manager, and reports your dedicated graphics card even when an integrated GPU is also enabled.

WinScope shows its own CPU use in the status line, so you can check it's staying light.

---

## Network

A modern replacement for Resource Monitor's network tab.

- **Download and upload speed** across your real network adapters (Wi-Fi and Ethernet), with 30 minutes of history.
- **Apps table**: each app's established connections, listening ports and UDP endpoints.
- **Connections**: select an app to see who it's talking to, with the protocol, ports, state, and a **Local / LAN / Internet** tag. A filter box searches app names, addresses and ports. A "Listening ports" view shows which apps accept incoming connections.
- **Per-app speeds** (administrator only): each app's download and upload speed and its total for the session, measured with Windows' built-in network tracing.

WinScope never contacts anything on the network and does no DNS lookups.

---

## Events

Event Viewer logs everything and explains nothing. WinScope picks out what matters and says it in plain English.

| Category | Examples |
|---|---|
| **Crashes & freezes** | Blue screens (with the stop code explained), unexpected shutdowns, apps crashing or not responding |
| **Graphics & drivers** | "Display driver stopped responding and has recovered" (TDR), NVIDIA and AMD driver errors |
| **Hardware** | WHEA hardware errors (corrected or fatal), disk and file system errors, storage controller resets, device problems |
| **Network** | Wi-Fi disconnects and failed connections, Intel Wi-Fi driver resets, Ethernet link drops |
| **Updates** | Windows updates that failed (with the error explained) or installed |
| **Power & startup** | Start-ups, shutdowns, sleep and wake (with what woke the PC), restarts and who started them |
| **Services** | Windows services that crashed or failed to start |

- Each item has an explanation, **what you can try**, and the raw details (log, source, event ID, full message) behind an expander.
- **One problem shows as one item.** A blue screen and its "unexpected shutdown" records are merged, and repeats within 10 minutes collapse into "×N".
- **Known-harmless warnings** that every Windows PC logs, such as DCOM 10016, are hidden by default, with a toggle to show them.
- **Filters**: last 24 hours, 7 days or 30 days; by category; a search box; and toggles for info events and harmless warnings.

No admin rights are needed. WinScope reads the System and Application logs.

---

## Storage

**Drives:** every drive, with used and free space.

**Space analyser:** scan a drive or folder to see what's taking the space. Folders are sorted by size and you can drill into them. A side panel lists the largest files, with "Show in Explorer" and "Move to Recycle Bin". The scan never follows links (junctions or symlinks), and online-only OneDrive files are counted separately because they don't use disk space.

**Cleanup: preview, then clean.**

| Category | What's removed | Default |
|---|---|---|
| Your temporary files | Files in your Temp folder older than 24 hours | On |
| Windows temporary files | `C:\Windows\Temp`, older than 24 hours (admin) | On |
| Browser caches | Only the cache folders of Chrome, Edge, Brave and Firefox. Never history, cookies or passwords. Skipped while the browser is open | On |
| Error reports | Windows Error Reporting files | On |
| App crash dumps | `%LocalAppData%\CrashDumps` | On |
| Thumbnail cache | Explorer's thumbnail database (rebuilt automatically) | On |
| Recycle Bin | Empties it | Off |
| Shader caches | DirectX, NVIDIA and AMD shader caches. Games rebuild them, but the first launch afterwards may stutter | Off |
| System crash dumps | Blue-screen dumps (admin). Keep these if you're troubleshooting crashes | Off |
| Windows Update cache | Downloaded update files (admin), only when Windows Update isn't running | Off |

Before anything is deleted:

1. You see each category's size, file count and largest files, and every folder it will touch.
2. You tick what you want and confirm a summary of exactly what will be deleted.
3. Every file is re-checked at the moment of deletion. It must still be inside that category's folder and still match its rules (for example, older than 24 hours), and it must not be a link. Files in use are skipped and reported.

Your own files (from the analyser) only ever go to the **Recycle Bin**, one at a time, after a confirmation. Nothing is ever deleted from the dashboard.

---

## Reports

Having a problem? Click **Report ▾** (top right) and choose:

- **Full system report**: this PC (Windows version, CPU, graphics, memory, uptime), the **key findings** from every tool, then each tool's details.
- **A single tool's report**: Performance, Network, Events or Storage on its own.

Reports are plain text written to be read by a person or an AI assistant, so you can paste one into ChatGPT, Claude, a forum post or an email and ask "what's wrong with my PC?". They're built from what WinScope has already collected, so they're instant. They're most useful after WinScope has been running for a while, or straight after the problem happens.

**Settings (⚙):**
- **When you make a report:** copy it to the clipboard (default), save it as a text file in `Documents\WinScope Reports`, or both.
- **Hide personal details** (on by default): removes user, account and PC names, email addresses, Wi-Fi network names, hardware (MAC) addresses, device serial numbers, internet IP addresses, Windows account IDs, and the names of your personal files and folders (a big video in Videos shows as `%USERPROFILE%\Videos\…\(name hidden).mp4`). Home-network addresses like 192.168.x.x are kept, because they're the same on millions of networks and help with diagnosis. Making a report never asks for location access.

Read a report before you share it. Turn off *Hide personal details* only for reports you keep to yourself.

---

## Administrator features

WinScope runs as a normal user. Two things need administrator rights because Windows only allows them to admin tools:

| Feature | Without admin | With admin |
|---|---|---|
| Network | Total speed, connections per app | Plus each app's download and upload speed |
| Storage cleanup | Your own temp, cache and crash files | Plus Windows temp files, system crash dumps and the Windows Update cache |

Click **Restart as admin** (top right, or in the tool) to get a Windows prompt. WinScope then reopens on the same tool. A few things are deliberately different while elevated:

- **Settings changes aren't saved.** WinScope won't write to your user folder as administrator, because that folder could be redirected by other software running as you.
- **Move to Recycle Bin is turned off.**
- **Wrong account protection:** if the elevated copy runs as a *different* account (another admin's password at the UAC prompt, or Windows 11 *Administrator protection*), cleanup of personal files is turned off, so it can't clean the wrong person's files.

**Tip:** if you use Restart as admin, keep WinScope.exe in a folder only administrators can change (for example `C:\Program Files\WinScope\`). Otherwise other software running as you could replace it before you elevate.

---

## Getting started

1. [Download `WinScope.exe`](https://github.com/geodnb/Winscope/releases/latest/download/WinScope.exe) from the [latest release](https://github.com/geodnb/Winscope/releases/latest).
2. Double-click it. No installation or admin rights are needed.
3. **First run:** because the exe isn't code-signed, Windows SmartScreen may warn you. Click **More info**, then **Run anyway**.

Only one copy of WinScope runs at a time.

---

## Files WinScope creates

| Location | Contents |
|---|---|
| `%AppData%\WinScope\settings.json` | Window size, last tool and report settings |
| `%AppData%\WinScope\events.json` | Events filters |
| `%AppData%\WinScope\cleanup-log.txt` | One line per cleanup: what, how much, what was skipped |
| `%AppData%\WinScope\error.log` | Only if something goes wrong (size-limited). Useful for bug reports |
| `Documents\WinScope Reports\` | Reports, only if you choose to save them as files |

Nothing is written there while WinScope runs as administrator. To remove WinScope completely, delete the exe and that folder.

---

## Accuracy and limitations

- **CPU %** is "% Processor Time". Task Manager on Windows 11 shows "% Processor Utility", which reads higher when the CPU is boosting.
- **Per-app "I/O"** includes all of an app's input/output (disk, network and devices), not just disk.
- **Per-app network speeds** count the data apps send and receive. They're a little lower than the adapter totals (no packet overheads, no loopback), and traffic from virtual machines or WSL isn't attributed to an app.
- **Events** are only as good as what Windows logs. Some explanations are best guesses where Windows' own data is vague, and the raw details are always one click away.
- **Storage sizes** are file sizes, not space used on disk, and hard-linked files (common in `C:\Windows\WinSxS`) are counted each time they appear.
- History isn't saved between runs.

---

## Privacy and safety

- **No internet access, telemetry or update checks.** WinScope makes no network connections of any kind. Reports go only where you put them: the clipboard, or a file on your PC.
- **Read-only, except Storage cleanup**, which only deletes what you've previewed and confirmed, within fixed folders and with the checks described above.
- **No admin rights by default.** Elevation is only ever at your request, and the elevated copy refuses to write to your user folder.
- **No third-party packages.** It's just .NET and Windows' own APIs.
- **DLL safety.** WinScope's own Windows API calls load DLLs only from System32.

---

## Building from source

Requires the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0).

```powershell
git clone https://github.com/geodnb/Winscope.git
cd Winscope\src

# Run from source
dotnet run -c Release

# Build a single, self-contained WinScope.exe (no .NET install needed to run it)
dotnet publish -c Release -r win-x64
# Output: src\bin\Release\net8.0-windows10.0.19041.0\win-x64\publish\WinScope.exe
```

| Folder | Contents |
|---|---|
| `src/Core/` | The tool contract, settings, elevation, safe file writing |
| `src/UI/` | Shared controls, colours and formatting |
| `src/Modules/Performance/` | Process and GPU sampling, history, spikes and graphs |
| `src/Modules/Network/` | Adapter counters, connection tables, network tracing, views |
| `src/Modules/Events/` | Log reading, the plain-English rules, merging, views |
| `src/Modules/Storage/` | Space analyser, cleanup catalogue, safe deletion, views |
| `src/Themes/` | Dark theme (shared with AirScope) |

---

## Disclaimer

WinScope is provided **"as is", without warranty of any kind**, express or implied, including but not limited to the warranties of merchantability, fitness for a particular purpose and non-infringement.

- **Use at your own risk.** In no event shall the author or contributors be liable for any claim, damages or other liability, whether in contract, tort or otherwise, arising from, out of or in connection with the software or its use. This includes, without limitation, loss of data, deleted files, system issues, or any decisions made based on its readings.
- **Cleanup deletes files.** Review the preview before confirming. Temporary and cache files are deleted permanently; your own files go to the Recycle Bin.
- **Not professional advice.** Event explanations and suggestions are informational. They are not a substitute for advice from the hardware maker, Microsoft support or a qualified technician.
- **Third-party names.** Windows is a trademark of Microsoft Corporation. NVIDIA, AMD, Intel and other names belong to their respective owners. WinScope is an independent project and isn't affiliated with or endorsed by any of them.

---

## Licence

WinScope is licensed under the [PolyForm Noncommercial License 1.0.0](LICENSE.md).

You're free to use, study, modify and share it for **non-commercial** purposes. You may **not** sell it, or use it or derivatives of it commercially, without permission.
