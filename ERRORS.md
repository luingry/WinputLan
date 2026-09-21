# Errors and prevention

## 2026-09-21 - Build without restore

- Symptom: `dotnet build --no-restore` reported missing `obj/project.assets.json`.
- Root cause: the new solution had not completed its first NuGet assets restore.
- Resolution: run `dotnet restore` once before no-restore Release builds.
- Prevention: build scripts restore explicitly and CI uses a restore/build step.

## 2026-09-21 - Enum validation type mismatch

- Symptom: framing smoke failed at runtime while validating a decoded frame.
- Root cause: `Enum.IsDefined` was passed `Int32` for byte-backed protocol enums.
- Resolution: validate using the original byte values.
- Prevention: frame and input round-trip tests run after every Release build.

## 2026-09-21 - Fixed input record size

- Symptom: input round-trip smoke rejected its own payload as the wrong length.
- Root cause: the wire record reserves 32 bytes but the encoder initially emitted 28.
- Resolution: emit and consume a four-byte reserved field.
- Prevention: the protocol test asserts an encoded input can be decoded before transport work is accepted.

## 2026-09-21 - Net48 runtime references and sequence counter

- Symptom: WPF Release build failed on `HttpClient`, config path resolution, and an unsupported `Interlocked` overload.
- Root cause: SDK-style net48 requires an explicit `System.Net.Http` reference; a `Path` property shadowed `System.IO.Path`; .NET Framework has no `Interlocked.Increment(ref ulong)` overload.
- Resolution: added the framework reference, qualified the path call, and used an atomic `long` sequence converted to the wire `ulong`.
- Prevention: the Release WPF build is part of the validation script.

## 2026-09-21 - TLS loopback handshake and receive-loop startup

- Symptom: the first loopback pairing could not start a TLS session or never exchanged a hello frame.
- Root cause: Schannel requires a persisted private-key certificate and mutual TLS certificates; the receive loop also began synchronously on the caller before the connect task could return, and pairing hello was sent during the pre-TLS `Pairing` state.
- Resolution: reload generated PFX material with a persisted user key, require/send certificates on both TLS sides, run the receive loop on the thread pool, and only send hello after the `TLS ativo` state.
- Prevention: the net48 loopback harness pairs two independent certificates, confirms identical SAS values bilaterally, and transports a synthetic input frame.

## 2026-09-21 - Backoff cap test mismatch

- Symptom: the release test expected a 15-second ceiling while the formula stopped growing at 4 seconds.
- Root cause: the exponent clamp was too low for the documented cap.
- Resolution: allow the exponential sequence to reach the 15-second cap and retain the cap for later attempts.
- Prevention: the backoff test covers both monotonic growth and the ceiling.

## 2026-09-21 - GitHub host suffix validation

- Symptom: a lookalike host ending in `github.com` would pass a naive suffix check.
- Root cause: host validation did not require the exact GitHub host or a dot-delimited subdomain.
- Resolution: allow only `github.com`/`githubusercontent.com` and their dot-delimited subdomains; add a regression test.
- Prevention: updater manifest tests include a lookalike host case.

## 2026-09-21 - Input fail-safe abstraction

- Symptom: broadening the router sink seam for receiver tests left cleanup coupled to `SendInputSink`.
- Root cause: the cleanup method was not represented by the sink contract.
- Resolution: added `IFailSafeInputSink`; physical injection implements it and test sinks remain side-effect free.
- Prevention: transport-loss tests cover both queue deactivation and the fail-safe capability.

## 2026-09-21 - Icon normalizer image runtime

- Symptom: the deterministic icon normalizer could not compile `System.Drawing` under PowerShell Core.
- Root cause: the installed .NET runtime exposes only the `System.Drawing` type-forwarder, while the Windows image APIs used for PNG/ICO production are provided by .NET Framework.
- Resolution: the script re-invokes itself through Windows PowerShell before loading `System.Drawing`.
- Prevention: run `scripts/normalize-icon.ps1` directly; it selects the compatible runtime itself.
