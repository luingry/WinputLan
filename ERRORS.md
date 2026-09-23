# Errors and prevention

## 2026-09-21 - OTA bloqueado em instalações públicas sem Authenticode

- Sintoma: o updater recusava qualquer setup quando o executável instalado não tinha um certificado Authenticode comercial confiável.
- Causa raiz: a política vinculava a confiança da atualização ao thumbprint do binário bootstrap, em vez de autenticar os metadados da release.
- Solução: manifesto canônico assinado com RSA PKCS#1 v1.5/SHA-256, chave pública fixada no aplicativo, hash do setup e validação estrita de versão, host, nome e URLs.
- Prevenção: manter o PEM privado apenas no secret `OTA_SIGNING_PRIVATE_KEY_PEM`; testes devem rejeitar adulteração de todos os campos assinados e metadados de algoritmo/chave.

## 2026-09-21 - Movimento remoto preso após vincular

- Sintoma: ao controlar outra máquina, o cursor local do controlador ficava parado e o alvo recebia coordenadas quase idênticas, causando flicker.
- Causa raiz: o hook de mouse suprimia `WM_MOUSEMOVE` após publicar, impedindo o cursor local de avançar e ancorando as coordenadas absolutas seguintes.
- Solução: movimentos publicados sempre seguem para o Windows; apenas cliques e roda publicados continuam suprimidos. Ao receber um pedido para agir como alvo, qualquer captura outbound anterior é desativada.
- Prevenção: a política testável de supressão exige `false` para movimento e para qualquer evento não publicado; validar fisicamente em dois PCs antes de declarar aceitação de hardware.

## 2026-09-21 - Executable avulso sem dependências de runtime

- Sintoma: `I:\Downloads\WinputLan.exe` não abria e o Windows registrava `System.IO.FileNotFoundException` em `WinputLan.App.OnStartup`.
- Causa raiz: a distribuição copiou apenas o executável de um aplicativo WPF .NET Framework dependente de `WinputLan.Core.dll` e `WinputLan.exe.config`.
- Solução: distribuir os três artefatos da mesma build juntos e verificar os hashes antes do teste de abertura.
- Prevenção: a cópia manual para Downloads deve preservar o conjunto de runtime (`.exe`, `.dll` e `.exe.config`); para entrega a terceiros, usar o instalador/release que empacota esse conjunto, incluindo explicitamente o arquivo `.exe.config`.

## 2026-09-21 - Aspas inválidas nos parâmetros do firewall do Inno Setup

- Sintoma: `ISCC.exe` interrompia a compilação com `Mismatched or misplaced quotes on parameter "Parameters"`.
- Causa raiz: o script usava `\"`, que não escapa aspas em valores de diretiva do Inno Setup.
- Solução: substituir pelas aspas duplas do Inno Setup (`""`) nos parâmetros de criação e remoção da regra de firewall.
- Prevenção: sempre compilar o `.iss` depois de alterar diretivas `[Run]` ou `[UninstallRun]`; escapes de shell não se aplicam ao parser do Inno Setup.

## 2026-09-21 - Release executable locked by visual smoke

- Symptom: Release build could not replace `WinputLan.exe` while a prior smoke instance was open.
- Root cause: Windows holds the executable image handle for the running WPF process.
- Resolution: stop only the smoke instance before rebuilding, then launch the freshly built binary for capture.
- Prevention: the smoke sequence is build → launch → capture → close; never rebuild over a live smoke process.

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

## 2026-09-21 - Pareamento bilateral e papéis de entrada recursivos

- Sintoma: o fluxo exigia confirmação dos dois PCs, não expunha IP/código para uma solicitação simples, e o X do popup podia deixar uma solicitação pendente. Depois de conectar, ambos os lados também podiam capturar/enviar entrada, abrindo feedback/loop.
- Causa raiz: o coordenador modelava SAS bilateral em vez de solicitação controller→target; a UI não vinculava cancelamento ao estado da requisição; e o transporte não tinha direção de entrada explícita por sessão.
- Solução: o alvo exibe IP e código de alta entropia, valida challenge-response antes do popup passivo, e somente o aceite alvo habilita controller-send/target-receive. X/Escape/cancelar cancelam o outbound ou negam o inbound, incluindo cancelamento remoto pendente. O transporte publica TCP/TLS somente quando sua lease de geração ainda é corrente, impedindo que uma tentativa cancelada tardia substitua a conexão nova.
- Prevenção: manter os testes de challenge-response (código/prova inválidos e substituição de certificado não abrem popup), aceite/negação, sessão one-way com frame proibido, cancelamento que torna `AcceptPending` inválido e o loopback A atrasada/B corrente que rejeita publicação tardia.

## 2026-09-21 - Latência degradada por polling, flush e repintura no hot path

- Sintoma: comandos de entrada aguardavam polling de 2 ms, cada frame fazia flush adicional, e eventos de mouse podiam disparar logging/repintura da DataGrid para cada comando, elevando latência e consumo de UI.
- Causa raiz: fila sem sinalização de disponibilidade, flush redundante após `WriteAsync`, e auditoria visual sem filtro/rate limit para eventos de alta frequência.
- Solução: a fila passou a aguardar sinal, o flush redundante foi removido, e telemetria/log de mouse usa filtro e atualização agregada de no máximo quatro vezes por segundo; ACK timestampado mede a rota útil.
- Prevenção: preservar o benchmark loopback com a mesma sessão TLS/30 eventos/ACK comparando polling legado de 2 ms contra drain signal-driven e assert de p95 local <=50 ms, além dos testes de coalescência/clear, janela de latência e throttle de auditoria.
