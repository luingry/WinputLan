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

Tudo acontece dentro da sua rede local. Não há conta, nuvem nem coleta de dados, e cada conexão precisa ser aceita no PC controlado.

## Atualizações

O app procura atualizações sozinho (diariamente, semanalmente ou nunca, ajustável na tela principal) e se reinstala automaticamente.

## Desenvolvimento

```powershell
pwsh -File scripts/build.ps1 -Configuration Release
```

Para publicar uma versão, veja [docs/RELEASING.md](docs/RELEASING.md).
