# Releasing

## Fixed point

`VERSION` is the single source of truth and starts at `0.1.0`. Every release
uses SemVer and a Keep a Changelog entry. The expected repository is
`luingry/WinputLan`; this project does not create remotes or publish releases.

## Local release validation

```powershell
powershell -ExecutionPolicy Bypass -File scripts/finalize.ps1
powershell -ExecutionPolicy Bypass -File scripts/validate-release.ps1
```

The scripts restore/build Release, run unit and loopback tests, copy the
framework-dependent payload, calculate SHA-256, and emit a manifest. The Inno
Setup file creates the explicit Private TCP rule and packages the executable,
core DLL, normalized icon, and version metadata.

If Inno Setup is installed, run `scripts/build-installer.ps1` to compile the
`.iss` file. The current development host does not have `ISCC.exe`, so no
installer binary is claimed as locally built; the script fails clearly instead
of silently producing a portable-only release.

## GitHub Actions

`ci.yml` builds and tests on Windows. `release.yml` runs on a `v*` tag, rebuilds
from that tag, uploads setup/portable payloads and the SHA-256 manifest, and
derives release notes from the matching changelog heading. Configure the
repository's signing certificate/secret before enabling production publishing.

## Authenticode and SmartScreen

Unsigned development output is expected to trigger SmartScreen. Do not work
around it in documentation or code: sign the installer and executable with an
organization-controlled Authenticode certificate, publish the certificate
chain, and preserve signer continuity. The updater refuses an asset unless its
manifest requires Authenticode and the local verifier accepts the signature.

## Rollback

Keep the previous setup executable and manifest. If a new release fails hash,
signature, pairing, or input cleanup checks, stop publication and point the
release channel back to the last signed manifest. Never replace an installed
binary without hash and signature validation.
# Signed setup release

Releases are intentionally fail-closed: CI requires `SIGNING_PFX_BASE64` and `SIGNING_PFX_PASSWORD`, signs and verifies `WinputLan.exe`, compiles the Inno setup, then signs and verifies `WinputLan-<version>-setup.exe`. `finalize.ps1` emits the setup-only updater manifest and matching SHA-256. A local package build without a certificate or ISCC is not a release and stops with an explicit error.

`VERSION` is the sole product-version source via `Directory.Build.props`; `app.manifest` retains its Windows assembly-identity schema value and is not used for product/update versioning.
