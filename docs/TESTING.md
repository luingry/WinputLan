# Testing and evidence

## Automated

`tests/WinputLan.Tests` is a dependency-light net8 console suite. It covers:

- frame and input binary round trips plus malformed bounds;
- symmetric transcript, SAS, pin code, HKDF and bilateral confirmation;
- DPAPI abstraction round trip and corrupt pin fail-safe;
- queue capacity, mouse-only coalescing, and key/button order;
- global hotkey parsing and reserved combinations;
- bounded metadata-only logging;
- corrupt/invalid config rejection;
- newer SemVer, GitHub HTTPS, SHA-256 and Authenticode manifest invariants;
- capped reconnect backoff.

The net48 loopback harness starts two independent transports and certificates,
performs TLS mutual authentication, receives equal SAS values, confirms both
sides, emits pin records, and sends one synthetic key frame to a fake sink. It
does not call a physical keyboard or mouse.

## Commands

```powershell
dotnet build WinputLan.sln -c Release
dotnet run --project tests/WinputLan.Tests/WinputLan.Tests.csproj -c Release --no-build
tests/WinputLan.Loopback/bin/Release/net48/WinputLan.Loopback.exe
```

The WPF process was launched for a startup smoke in the development
environment. Full screenshot capture is environment-dependent; the source
UI, accessibility names, keyboard focus triggers, and generated direction
assets are checked into the repo. Physical two-PC input acceptance, UIPI/UAC,
and signed-update acceptance remain release-gate work.
