# Bundled PawnIO installer provenance

`PawnIO_setup.exe` is the unmodified embedded resource
`LibreHardwareMonitor.Resources.PawnIO_setup.exe` extracted from the official
LibreHardwareMonitor 0.9.6 Windows release.

- LibreHardwareMonitor release archive SHA-256: `086D9F1B5A99E643EDC2CFAAAC16051685B551E4C5AC0B32A57C58C0E529C001`
- PawnIO installer version: `2.1.0.0`
- PawnIO installer SHA-256: `A3A46226C5E2824F4CDD42BE0EECBABFC672C86F7889710F5AB1E6AD385B47A0`
- Authenticode status at extraction: `Valid`
- Authenticode signer: `namazso.eu`
- Upstream: <https://github.com/namazso/PawnIO>
- Setup upstream: <https://github.com/namazso/PawnIO.Setup>
- License: GPL-2.0 with the PawnIO linking exception; modules are LGPL-2.1

The Inno Setup package verifies the installer SHA-256 before executing it with
`-install -silent`. It skips installation when a registered compatible PawnIO
2.x version is already present and does not uninstall this shared dependency
when Performance Monitor is removed.
