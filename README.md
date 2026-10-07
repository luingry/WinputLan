# Winput LAN

Use um único mouse e teclado para controlar vários PCs Windows na mesma rede local.

## Como usar

1. Baixe e execute `WinputLan-<versão>-setup.exe` no Windows 10 ou 11.
2. Abra o **Winput LAN** nos dois PCs.
3. No PC que será controlado, anote o IP e o código exibidos.
4. No outro PC, clique em **Conectar outra máquina** e informe o IP e o código.
5. Aceite o pedido no PC controlado.

Depois da primeira conexão, os PCs se reconhecem: basta clicar na máquina (ou usar o atalho) e aceitar no outro PC.

## Dicas

- **Atalhos:** alterne entre os PCs com `Ctrl + Shift + Alt + 1` e `Ctrl + Shift + Alt + 2` (dá para mudar em **Editar atalhos**).
- **Iniciar com o Windows:** o app abre minimizado na área de notificação ao entrar no Windows.
- **Apps de administrador:** para controlar janelas como o Gerenciador de Tarefas, marque **Permitir controlar apps de administrador** no PC controlado e confirme o aviso do Windows usando o mouse e o teclado desse PC.
- **Avisos do Windows (UAC):** essas telas só aceitam o mouse e o teclado físicos do próprio PC.

## Privacidade

O controle de mouse e teclado acontece dentro da sua rede local, sem conta ou serviço de controle na nuvem. Cada conexão segue as suas preferências de aceite.

Ao escolher **Reportar problema**, o aplicativo prepara automaticamente um diagnóstico técnico local, sem exibir esse bloco no formulário. A descrição e o diagnóstico são enviados à infraestrutura Cloudflare do projeto somente ao clicar em **Enviar relato**. O diagnóstico inclui versões, monitores, preferências operacionais e eventos recentes; exclui conteúdo digitado, nomes dos PCs, endereços de rede e segredos de pareamento. Relatos ficam armazenados por até 90 dias. O formulário não salva nem recupera rascunhos; fechar a janela descarta o texto.

O formulário abre dentro do app e usa o Microsoft Edge WebView2 Runtime para a verificação antibot, que aparece somente quando exige interação manual. Se ele estiver ausente, a janela informa o requisito. Falhas temporárias da verificação são repetidas automaticamente enquanto a janela estiver aberta.

## Atualizações

O app procura atualizações sozinho (diariamente, semanalmente ou nunca, ajustável na tela principal) e se reinstala automaticamente.

## Desenvolvimento

```powershell
pwsh -File scripts/build.ps1 -Configuration Release
```

Para publicar uma versão, veja [docs/RELEASING.md](docs/RELEASING.md).

Para configurar e operar o serviço de relatos, veja [docs/BUG_REPORTS_SETUP.md](docs/BUG_REPORTS_SETUP.md).
