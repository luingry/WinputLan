import {mkdir,writeFile} from 'node:fs/promises';
import {buildReportEmail} from '../report-email.js';

// Synthetic fixture only; generates a local preview without submitting a report.
const report={id:'0123456789abcdef0123456789abcdef',capturedUtc:'2026-10-07T12:30:00Z',
 title:'Cursor parou ao trocar de máquina',
 description:'Ao mover o cursor para a borda direita, o controle não passou para a outra máquina.\n\nA conexão continuou ativa e o atalho de troca funcionou normalmente.',
 steps:'1. Conectar as duas máquinas.\n2. Ativar a troca pela borda direita.\n3. Mover o cursor até a borda.',
 diagnostics:{metadata:{appVersion:'0.3.28',windowsVersion:'10.0.26100',architecture:'x64',elevated:false,outboundState:'Connected',inboundState:'Offline'},monitors:[{x:0,y:0,width:1920,height:1080,primary:true}],events:[]}};
const message=buildReportEmail(report),logo=message.attachments[1];
const output=new URL('../../../artifacts/report-email/',import.meta.url);
await mkdir(output,{recursive:true});
// CID is resolved by mail clients; use the same attachment bytes in the browser preview.
await writeFile(new URL('preview.html',output),message.html.replace('cid:'+logo.contentId,'data:'+logo.type+';base64,'+Buffer.from(logo.content).toString('base64')));
await writeFile(new URL('preview.txt',output),message.text);
console.log('Prévia local: '+new URL('preview.html',output).pathname);
