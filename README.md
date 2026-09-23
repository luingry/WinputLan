# Winput LAN

Controle mouse e teclado de um PC Windows para outro na mesma rede local.

## Instalar e usar

1. Baixe e execute `WinputLan-<versão>-setup.exe` no Windows 10 ou 11.
2. Abra **Winput LAN** pelo Menu Iniciar em ambos os PCs.
3. No PC controlado, copie o IP e o código exibidos.
4. No controlador, use **Controlar outra máquina**, informe IP e código.
5. O PC controlado aceita ou nega o pedido. A sessão aceita envia entrada em uma única direção.

O Windows bloqueia controle remoto em janelas de administrador (ex.: Gerenciador de Tarefas). Para controlá-las, marque **Permitir controlar apps de administrador** no PC controlado. Avisos de segurança do UAC só aceitam mouse e teclado físicos.

O instalador cria uma regra de firewall apenas para a rede Privada. O programa pode continuar na área de notificação ao minimizar ou fechar, se essa opção estiver habilitada.

## Segurança e atualizações

O pareamento exige código e aceite local. A comunicação é TLS na LAN; não há descoberta, nuvem ou telemetria.

Cada atualização OTA baixa um manifesto HTTPS do GitHub, valida uma assinatura RSA fixada no aplicativo, exige versão mais nova e confere o SHA-256 do instalador. A assinatura do manifesto protege a atualização mesmo quando o setup não possui certificado comercial. O Windows SmartScreen ainda pode avisar sobre um instalador sem Authenticode.

O app procura atualizações sozinho (diariamente, semanalmente ou nunca — ajustável na tela principal), mostra o progresso do download, instala em silêncio e reabre ao final.

## Build e release

```powershell
pwsh -File scripts/build.ps1 -Configuration Release
pwsh -File scripts/new-ota-signing-key.ps1 # uma vez, fora do repositório
pwsh -File scripts/finalize.ps1 -Configuration Release
```

Para uma release do GitHub, guarde o PEM criado exclusivamente no secret `OTA_SIGNING_PRIVATE_KEY_PEM`, atualize `VERSION` e `CHANGELOG.md`, e publique a tag correspondente. Consulte [docs/RELEASING.md](docs/RELEASING.md) para o fluxo completo.
