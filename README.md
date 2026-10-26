# Proxy Deck

A lightweight Windows tray app that starts CLIProxy on demand and shows your
Codex accounts' remaining quotas. The first-run wizard installs missing CLIProxy
components and offers optional Tailscale access and Auto Warm. Proxy Deck does
not start automatically with Windows.

## Installation

[Download Windows installer](https://github.com/dynamarley/proxy-deck/releases/latest/download/ProxyDeck-Setup.exe)
| [Download ZIP](https://github.com/dynamarley/proxy-deck/releases/latest/download/ProxyDeck-Windows-x64.zip)
| [All releases](https://github.com/dynamarley/proxy-deck/releases)

1. Download **`ProxyDeck-Setup.exe`** using the installer link above and
   open it. If you download the ZIP, extract the entire archive first.
2. Choose a CLIProxy folder. If CLIProxy is missing, the wizard installs it. For
   an existing setup, select the folder containing `cli-proxy-api.exe` and
   `config.yaml`, then enter its **management key**. This is different from the
   client API key.
3. Optionally enable **Tailscale** and **Auto Warm**. Both are off by default for
   new installations.
4. Review your choices and select **Install & prepare**.
5. Select **Connect Codex account** and sign in through your browser. Repeat for
   each account you want to use.
6. Open the **Proxy Deck** desktop shortcut whenever you need it. Click its
   system tray icon to see remaining quotas and reset times.

The app installs under `%LOCALAPPDATA%\ProxyDeck`. New CLIProxy installations
default to `%LOCALAPPDATA%\CLIProxyAPI`. End users do not need Git, PowerShell
commands, or a source build.

**Requirements:** Windows 10/11 **x64**, [.NET Framework 4.8](https://dotnet.microsoft.com/download/dotnet-framework/net48),
an internet connection for initial downloads, and a supported Codex account.
ARM64 and 32-bit Windows are not supported in this release. The Proxy Deck
interface and documentation are in English. Windows administrator approval is
needed only for optional Tailscale installation and service setup; daily use
does not require elevation.

Proxy Deck releases are currently unsigned. Windows SmartScreen or an
organization's device policies may restrict installation.

## Components

| Component | Purpose | Installation behavior |
| --- | --- | --- |
| [CLIProxyAPI](https://github.com/router-for-me/CLIProxyAPI) | Exposes your accounts through a local, OpenAI-compatible API | Downloads the verified `8.0.44` Windows package if missing; does not automatically replace an existing executable |
| [Codex Fleet Manager](https://github.com/doer-ee/cpa-plugin-codex-fleet-manager) | Reads remaining quotas and reset times | Installs the verified `0.4.3` plugin; account routing and model retries stay disabled |
| [Tailscale](https://tailscale.com/download/windows) | Private access to your dashboard from your phone | Installs or uses an existing installation only when selected |
| Auto Warm | Activates a new quota window with a small request when needed after reset | An optional Fleet Manager feature; no separate Windows scheduled task |

CLIProxy and Fleet Manager archives are downloaded directly from their official
GitHub releases and checked against pinned SHA-256 hashes. The official
Tailscale MSI must have a valid **Tailscale Inc.** Authenticode publisher
signature before it can run. Third-party binaries are not bundled in this
repository or the Proxy Deck release package. See
[THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) for project links and licenses.

## Tailscale options

- **Disabled:** Quota tracking and the local API work without Tailscale. An
  existing Tailscale installation is not managed.
- **Shared use:** Leave **Run Tailscale only while Proxy Deck is open** unchecked
  if you use Tailscale for other tasks. Proxy Deck does not stop its service on
  exit or change its automatic startup setting.
- **On-demand use:** Check that option to set the service to manual startup and
  stop it when you exit Proxy Deck. Previous service settings are backed up.

Tailscale service setup enables background operation so a separate Tailscale
tray window is not required. You still need to sign in to Tailscale and enable
HTTPS/Serve for your tailnet. The wizard links to those steps; it does not
create accounts on your behalf. Sign in to the same tailnet on your phone.
**Copy phone link** discovers this computer's current address automatically.

CLIProxy remains bound to loopback. When Tailscale is selected for a new setup,
only loopback proxy headers are trusted and remote management still requires
the management key. The management API is not exposed directly to the public
internet. Automatic Tailscale configuration of an existing setup requires the
CLIProxy v8 configuration format; older layouts must be migrated first.

Fleet Manager's standalone dashboard defaults to English in a fresh browser
session. If you previously selected another language in that third-party
dashboard, use its language selector to switch back to English. Windows and
third-party sign-in pages may follow their own language preferences.

## Auto Warm recommendations

The recommended settings are **check 1 minute after reset**, probing on
unconfirmed rosters disabled, account routing disabled, and model retries
disabled. Fleet Manager reads the actual quota first. If needed, it sends one
small Codex request and verifies the result. It does not unconditionally send
requests to every account at fixed times.

To monitor all accounts together, give them the same CLIProxy account priority
(`0` is recommended). Fleet Manager monitors the highest confirmed priority
tier when priorities differ. Proxy Deck does not change account priorities
automatically. Warmup requests can use a small amount of quota and do not
guarantee that a new window will start or that additional quota will appear.
Your account provider's service terms apply.

**Auto Warm and the phone dashboard do not run while the system is stopped.**
You can enable or disable Auto Warm later under **Setup & components**.

## Daily use

- **Click the tray icon:** See each account's remaining 5-hour and weekly or
  monthly quota, along with reset times.
- **Tray number:** The highest remaining 5-hour percentage among monitored
  accounts. It is not a pooled quota. Unknown values appear as `—`.
- **Refresh:** Requests a quota check, at most once per minute while the panel
  is open. With the panel hidden, local status is read every 90 seconds. The
  footer shows how old the quota data is.
- **Quota dashboard:** Opens Fleet Manager's detailed dashboard. Use
  **Copy management key** if it asks for authentication.
- **Connect Codex account:** Adds an account through your browser.
- **Stop system:** Stops the managed proxy while leaving the tray app ready.
- **Exit and stop system:** Stops managed components and exits the tray app.
  The panel's **×** button only hides it.

Leave **Also manage the existing CLIProxy process** unchecked if you want an
existing proxy to keep running independently. Proxy processes started by Proxy
Deck itself are still stopped on exit. Avoid using another launcher or service
to manage the same proxy. Disable old startup tasks yourself in Windows; the
wizard does not delete other applications' scheduled tasks.

## T3 Code and other clients

The default OpenAI-compatible base URL is `http://127.0.0.1:8317/v1`. Choose
**Copy client API key** from the tray menu. Do not use the management key in
model clients. New installations generate two separate random keys.

Add a custom OpenAI-compatible provider in T3 Code or your preferred client,
then enter that address and the client API key. Select a model supported by
your CLIProxy setup. Proxy Deck does not automatically change T3 Code, Codex
CLI, or your existing model configuration.

## Updates, repair, and removal

Exit Proxy Deck from the tray before opening a new `ProxyDeck-Setup.exe`.
The previous app binary is saved as `.previous`, and your settings are kept.
Dependencies are not silently updated. **Setup & components** shows which
versions will be installed or verified.

You can rerun the wizard after a failed download. Completed, verified downloads
are reused. Before changing an existing YAML configuration, the wizard creates
a timestamped `.proxydeck-*.bak` backup beside it. Account files are not deleted.

To uninstall, open **Windows Settings → Installed apps → Proxy Deck → Uninstall**.
The uninstaller stops the managed system and removes Proxy Deck's program
files. Your accounts, keys, settings, CLIProxy, and Tailscale are kept; remove
those separately if needed. To restore previous Tailscale service and startup
settings, run `Restore-Autostart.ps1` as administrator using the same Windows
account. The script is included in the ZIP and installed app folder.

## Troubleshooting

| Problem | What to check |
| --- | --- |
| Port already in use | Close the other proxy launcher or choose a free port. For an existing setup, the wizard's port must match config.yaml. |
| Management key rejected | Enter the correct management key in the wizard. Automatic authentication retries are paused after a rejection; use Refresh after correcting the key. |
| No quotas displayed | Connect a Codex account and select Refresh. Check whether CLIProxy account priorities differ. |
| Tailscale is connected but the phone dashboard fails | Use the same tailnet on your phone. Windows, CLIProxy, and HTTPS/Serve must be running and ready. |
| Download or hash verification failed | Check your connection and try again. GitHub rate limits or network restrictions may delay installation. |
| Service permission missing | Rerun setup with Tailscale selected and complete the Windows administrator prompt. |
| Tray icon missing | Open the hidden-icons arrow and drag Proxy Deck onto the taskbar. |

The log is at `%LOCALAPPDATA%\ProxyDeck\proxy-deck.log`. Keys, upstream response
bodies, and OAuth tokens are not logged. Do not attach account files,
settings.json, or your CLIProxy configuration to public issue reports.

## Development and releases

No additional package manager is required. Build with the Windows .NET
Framework C# compiler:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\ProxyDeck\Build.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File .\ProxyDeck\Package.ps1
```

`artifacts/` contains the Setup EXE, ZIP, and SHA256SUMS file.
`--verify <report.json>` checks configuration editing, encryption, and archive
boundaries. `--integration-test <report.json>` downloads official dependencies,
installs an account-free CLIProxy in a temporary directory on a separate port,
and verifies local startup, shutdown, and warmup settings through the real API.
It does not change the live user installation or Tailscale service.

GitHub Actions builds on Windows and runs both verification modes. Pushing a
`v*` tag creates a GitHub Release after the checks pass. The workflow uses your
repository's address automatically. Update dependency versions and hashes
together in `ProxyDeck/Dependencies.cs`, then rerun the integration test.

Proxy Deck uses WinForms and .NET Framework. It does not require Electron,
WebView, or a Node runtime, and it includes no telemetry. This is an independent
project, not an official OpenAI, CLIProxyAPI, Fleet Manager, or Tailscale product.
