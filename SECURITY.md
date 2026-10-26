# Security

Do not attach credentials, OAuth files, unredacted proxy configuration, DPAPI
settings or service backups to public issues. Report with the app version,
Windows version, component versions and a minimal redacted error.

New installations bind CLIProxy to loopback, generate separate random management
and client keys, and store the recoverable copies with Windows CurrentUser DPAPI.
CLIProxy owns its OAuth storage and hashes the configuration management key.
DPAPI is not protection against another process running as the same Windows user.

Dependency archives are pinned and SHA-256 checked; Tailscale MSI installation
requires a valid Authenticode signature for Tailscale Inc. Downloads execute only
after their corresponding checks. Installer elevation is limited to the optional
Tailscale MSI/service setup. The application runs asInvoker.

Native CLIProxy plugins execute inside CLIProxy. Install plugins only from
maintainers you trust. Tailscale access is restricted by the user's tailnet ACLs
and still requires the CLIProxy management key.

Proxy Deck does not provide telemetry or a remote support endpoint. Its diagnostic
logs omit tokens and upstream HTTP bodies. Authentication errors stop automatic
credential attempts until the user explicitly retries.
