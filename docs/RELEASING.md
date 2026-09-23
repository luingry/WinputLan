# Releasing

`VERSION` is the release source of truth. Update it and add the matching `## [x.y.z]` section in `CHANGELOG.md`; `scripts/release-notes.ps1` publishes precisely that section.

## One-time key setup

Run PowerShell 7 locally:

```powershell
pwsh -File scripts/new-ota-signing-key.ps1
```

It writes the private PEM only to `%LOCALAPPDATA%\WinputLan\release\ota-private.pem` with a user-only ACL where Windows permits it. Never copy it into the repository, issue output, or release assets. Set its full PEM text as the GitHub Actions secret `OTA_SIGNING_PRIVATE_KEY_PEM`.

## Local validation

```powershell
pwsh -File scripts/build.ps1 -Configuration Release
pwsh -File scripts/finalize.ps1 -Configuration Release
pwsh -File scripts/validate-release.ps1
```

The finalizer builds/tests, compiles the unsigned Inno Setup installer, writes its SHA-256, and signs the deterministic OTA manifest. The published assets are the setup executable, `update-manifest.json`, `SHA256.json`, and release notes.

## GitHub release

The `release.yml` workflow runs only for a `v<VERSION>` tag. It fails closed without `OTA_SIGNING_PRIVATE_KEY_PEM`, creates the setup and signed manifest, then attaches `artifacts/WinputLan-<VERSION>/**` to the GitHub release. The updater accepts only the pinned GitHub hosts, an exact newer versioned setup name, signed canonical metadata, and matching SHA-256.

The installer and application are intentionally unsigned by default. Authenticode is optional and can improve SmartScreen reputation, but is not a substitute for the RSA OTA verification.
