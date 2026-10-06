# Relatos de problemas

O menu **Reportar problema** no dashboard e na bandeja abre uma janela WPF. A descrição, passos opcionais e diagnóstico são enviados ao clicar em **Enviar relato**. A verificação Turnstile usa WebView2 dentro da mesma janela. Nenhum navegador externo é aberto.

## Estado da entrega em 2026-10-06

| Item | Estado |
| --- | --- |
| Formulário, diagnóstico e recuperação local | Implementados e testados |
| Serviço, quotas, outbox e testes D1 locais | Implementados e testados |
| Email Routing `winputlan-feedback@luingry.com.br` | Regra criada para a caixa já verificada do proprietário |
| D1 `winputlan-bug-reports` | Criado; ID `bd3b96bb-3505-4217-9ca9-aa6ca0d730a5` |
| Widget Turnstile `Winput LAN reports` | Criado; domínio `bugs.luingry.com.br`, chave pública em `wrangler.jsonc` |
| Migration D1 remota | Executada; triggers de quota e outbox conferidos |
| Worker, secrets, domínio e cron | Publicados; HTTPS `/health` responde `ready:true`; cron a cada cinco minutos |
| Recebimento real do e-mail com anexo | Confirmado na caixa principal; anexo JSON aberto e conferido |
| Envio completo pela janela nativa | Confirmado: Turnstile automático, protocolo recebido, rascunho removido e e-mail com diagnóstico real recebido |

O primeiro teste de e-mail utilizou um relato sintético inserido administrativamente no D1. Em seguida, o envio pela janela nativa recebeu `WLR-dbb1135f37b94ed5bd2dcddb12c7baa0`; o e-mail chegou à caixa principal com o JSON de diagnóstico da versão 0.3.28. O resolvedor do roteador apresentou NXDOMAIN transitório durante a implantação; a resolução normal depois se recuperou, permitindo validar o fluxo sem alterar a configuração de rede do computador.

## Configuração ativa e manutenção

O serviço foi provisionado pelo plugin Cloudflare. Secrets permanecem no Worker; não há credenciais de Cloudflare ou e-mail dentro do aplicativo.

As notificações usam `winputlan-feedback@luingry.com.br` como remetente e a caixa do proprietário já verificada como destino interno. Esse destino é o mesmo usado pelo Email Routing do alias. Não é necessário verificar o alias para o envio ativo. O alias também foi cadastrado como destination address e permanece pendente; sua confirmação é opcional, caso futuramente se queira enviar diretamente ao alias em vez da caixa principal.

Para manutenção com Wrangler, usar Node.js 22 ou mais recente na pasta `services/bug-reports`. A configuração local `wrangler.production.jsonc` é privada e ignorada pelo Git: contém somente a restrição do binding ao destinatário verificado. Em um checkout novo, copiar `wrangler.jsonc` para esse arquivo e ajustar `send_email[0].destination_address` para a caixa verificada do proprietário. O arquivo público usa o alias como exemplo; não publicar endereços pessoais no Git.

```powershell
npm ci
npx wrangler login
npx wrangler d1 migrations apply winputlan-bug-reports --remote --config wrangler.production.jsonc
npx wrangler d1 execute winputlan-bug-reports --remote --config wrangler.production.jsonc --command "SELECT name,sql FROM sqlite_master WHERE type='trigger'"
npx wrangler secret put TURNSTILE_SECRET --config wrangler.production.jsonc
npx wrangler secret put IP_HASH_SECRET --config wrangler.production.jsonc
npx wrangler secret put EMAIL_TO --config wrangler.production.jsonc
npx wrangler deploy --config wrangler.production.jsonc
```

Os três secrets já estão configurados. `TURNSTILE_SECRET` é o secret do widget existente. `IP_HASH_SECRET` usa 32 bytes criptograficamente aleatórios. `EMAIL_TO` guarda o destinatário privado e deve coincidir com a restrição do binding. Não repetir os comandos `secret put` em uma manutenção comum: eles servem para instalação nova ou rotação deliberada. Nunca colocar chaves ou endereço pessoal no app, Git ou logs. O sitekey público e os IDs D1 não são credenciais.

A migração usa LF e statements completos de trigger para evitar o erro `7500: incomplete input` observado na API remota. É idempotente para permitir completar uma execução parcial. Conferir a existência de `report_quota` e `report_outbox` no D1 remoto antes de publicar.

O deploy configura `bugs.luingry.com.br` como Custom Domain, Workers.dev e previews desativados, bindings DB/EMAIL/ABUSE_RATE e cron a cada cinco minutos. A conta precisou registrar o namespace Workers `luingry-winputlan`; o script permanece desativado nesse subdomínio. O binding de e-mail permite somente a caixa verificada e o remetente de feedback. Preservar o tunnel QGen, MX e regras de outros endereços.

Depois do deploy, `https://bugs.luingry.com.br/health` deve responder `{"ready":true}`. Essa resposta verifica presença dos bindings/secrets, mas não prova envio de e-mail. Abrir o formulário, concluir a verificação e enviar um relato de teste. Conferir o protocolo no D1 e o e-mail com `winputlan-diagnostic.json`; só então publicar a versão do aplicativo.

## Dados coletados e retenção

- Versões do app, Windows e CLR; arquitetura e elevação do processo.
- Resolução, posição e escala de monitores; tipo da conexão de rede, sem endereços.
- Preferências operacionais, atalhos normalizados, estado de conexão/foco, latência e uso de memória.
- Até 500 eventos recentes com horário, tipo e status filtrados. Sem tecla, texto, coordenadas de entrada, nome do PC, IP/MAC, certificado, código de acesso ou segredo de pareamento.

O diagnóstico é um snapshot da abertura da janela. Um rascunho recuperado conserva seu snapshot original. Os detalhes podem ser vistos antes de enviar. O texto que o usuário escreve no formulário é enviado integralmente; a filtragem se aplica ao diagnóstico automático.

Rascunhos usam DPAPI CurrentUser, gravação atômica, expiração por 30 dias sem uso e não são enviados em segundo plano. Após a primeira tentativa, o conteúdo fica congelado para permitir repetição segura. É possível iniciar um novo relato. O servidor retém relatos por 90 dias; o cron limpa os dados expirados. A cópia recebida por e-mail segue a retenção da caixa do destinatário.

## Resistência a abuso e falhas

- Corpo limitado a 96 KiB também em streaming, prazo de leitura de 10 s, JSON estrito, sem uploads arbitrários ou payload comprimido.
- Turnstile verificado no servidor, vinculado a hostname, ação e ID do relato. Falhas recusam o envio e preservam o rascunho.
- Limite de entrada por IP no Worker e orçamento D1 de tentativas; IP usado somente como HMAC com rotação diária. O IP original chega à Cloudflare para transporte e verificação, mas não é salvo em `reports`.
- Quotas atômicas: 3 relatos/IP/hora, 10/IP/dia e 50 relatos/dia no total. Relato e outbox são gravados na mesma transação. O teto global mantém 90 dias de payloads máximos abaixo do limite de 500 MB do D1 gratuito, com margem para índices.
- UUID, segredo aleatório e hash do conteúdo tornam reenvios idempotentes. O serviço não expõe leitura pública de relatos.
- Destinatário, remetente e assunto fixos; texto simples e anexo JSON. Nenhum endereço, HTML ou URL fornecido pelo visitante controla a notificação.
- Outbox durável, lease contra crons simultâneos, até oito tentativas com backoff e orçamento de 100 tentativas de e-mail/dia. Falhas definitivas permanecem visíveis para o administrador; não descartam o relato.

O envio do e-mail não participa da transação D1. Uma queda depois de o provedor aceitar o e-mail e antes de marcar `sent` pode causar uma notificação repetida, identificável pelo mesmo protocolo. O app só confirma aceitação após a gravação durável; isso não significa que o e-mail já chegou.

Não existe garantia de imunidade a exploits ou disponibilidade ilimitada. Ataques distribuídos e quotas globais da plataforma podem interromper novos envios. Nessas situações o rascunho fica no PC. Manter o projeto no plano Free e revisar quotas antes de qualquer mudança para um plano pago. Não incluir observabilidade com payloads ou secrets.

O administrador consulta os relatos e a outbox no painel privado D1. Para inspecionar falhas definitivas:

```sql
SELECT report_id, attempts, next_attempt, sent, failed FROM outbox WHERE failed=1;
```

Depois de resolver a causa do envio, um relatório específico pode ser recolocado na fila com uma consulta parametrizada administrativa que zera `failed`, `attempts` e `lease_until`, e ajusta `next_attempt`. Não há endpoint público de reprocessamento ou administração.

## Validação

`scripts/build.ps1` verifica Core, DPAPI, privacidade, repetição do cliente, instância única e transporte TCP/TLS. `WinputLan.Loopback.exe --report-runtime-test` verifica o loader WebView2 real. `WinputLan.exe --report-smoke` abre somente o formulário com rascunho de teste separado, sem hooks, listener ou mudanças no pareamento.

`npm test` usa Miniflare/D1 reais para testar quotas concorrentes, transação report/outbox, rollback por falha da outbox, reenvios, limites de corpo/stream lento, Turnstile e recuperação de falhas no provedor de e-mail. O provedor e o CAPTCHA são simulados nesses testes; o envio real depende da validação remota descrita acima.

Fontes oficiais: [Email Service e envio gratuito a destinos verificados](https://developers.cloudflare.com/email-service/), [Workers API e anexos](https://developers.cloudflare.com/email-service/api/send-emails/workers-api/), [limites D1](https://developers.cloudflare.com/d1/platform/limits/).
