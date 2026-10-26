# Proxy Deck source

See the repository's [README.md](../README.md) for installation, usage,
dependencies, and development instructions.

- `Build.ps1` builds the Windows x64 application.
- `Package.ps1` creates the release EXE, ZIP, and SHA-256 checksums.
- `Install.ps1 -Launch` provides a build-and-launch shortcut for developers.
- `Restore-Autostart.ps1` restores backed-up Tailscale service settings.

End users should open `ProxyDeck-Setup.exe` from GitHub Releases.
