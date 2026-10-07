import {reportLogo} from './report-email-logo.js';

// The Workers binding sends string content as text. Pass PNG bytes so the MIME
// payload is the image itself, rather than the base64 source stored in the module.
const LOGO_BYTES = Uint8Array.from(atob(reportLogo.content),character=>character.charCodeAt(0));
const FONT = "font-family:'Segoe UI',Arial,Helvetica,sans-serif;";
const WRAP = 'word-wrap:break-word;overflow-wrap:anywhere;word-break:break-word;';
function escapeHtml(value) {
 return String(value).replace(/[&<>"']/g,character=>({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[character]));
}
function content(value) {
 // Soft wrap opportunities also protect older email renderers from long IDs/URLs.
 // Work on text before escaping so entities and Unicode code points stay intact.
 return String(value).split(/(\s+)/u).map(part=>{
  const characters=Array.from(part);
  if(characters.length<32)return escapeHtml(part);
  const chunks=[];for(let i=0;i<characters.length;i+=12)chunks.push(escapeHtml(characters.slice(i,i+12).join('')));
  return chunks.join('&#8203;');
 }).join('').replace(/\r\n|\r|\n/g,'<br>');
}
function capturedTime(value) {
 const date=new Date(value),iso=date.toISOString();
 return iso.slice(8,10)+'/'+iso.slice(5,7)+'/'+iso.slice(0,4)+' às '+iso.slice(11,19)+' UTC';
}
function section(title,body) {
 return `<h2 style="margin:28px 0 10px;color:#F1F5F2;${FONT}font-size:18px;line-height:25px;font-weight:600;">${title}</h2>
<p style="margin:0;color:#DBE5DF;${FONT}font-size:14px;line-height:22px;${WRAP}">${body}</p>`;
}
function diagnosticRow(label,value) {
 return `<tr><th scope="row" width="40%" align="left" valign="top" style="padding:10px 12px;border-bottom:1px solid #353D43;color:#B8C7BE;${FONT}font-size:13px;line-height:20px;font-weight:400;">${label}</th>
<td width="60%" valign="top" style="padding:10px 12px;border-bottom:1px solid #353D43;color:#F1F5F2;${FONT}font-size:13px;line-height:20px;${WRAP}">${content(value)}</td></tr>`;
}

// Read surface: inherit the desktop palette, logo and type hierarchy. Email layout
// uses tables/inline CSS; the fluid 600px container has a fixed Outlook fallback.
// All visitor text is escaped. The diagnostic JSON and plain-text version remain.
export function buildReportEmail(report) {
 const receipt='WLR-'+report.id,metadata=report.diagnostics.metadata;
 const summary=[['Versão do aplicativo',metadata.appVersion??'Não informada'],
  ['Windows',metadata.windowsVersion??'Não informado'],
  ['Arquitetura',metadata.architecture??'Não informada'],
  ['Processo elevado',typeof metadata.elevated==='boolean'?(metadata.elevated?'Sim':'Não'):'Não informado'],
  ['Conexão de saída',metadata.outboundState??'Não informada'],
  ['Conexão de entrada',metadata.inboundState??'Não informada'],
  ['Monitores',report.diagnostics.monitors.length],['Eventos registrados',report.diagnostics.events.length]];
 const steps=report.steps.trim()?content(report.steps):'Passos não informados.';
 const html=`<!doctype html>
<html lang="pt-BR">
<head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><title>Relato de problema · Winput LAN</title></head>
<body style="margin:0;padding:0;background-color:#101519;color:#DBE5DF;${FONT}">
<table role="presentation" width="100%" border="0" cellpadding="0" cellspacing="0" bgcolor="#101519" style="width:100%;border-collapse:collapse;mso-table-lspace:0pt;mso-table-rspace:0pt;">
<tr><td align="center" style="padding:24px 12px;">
<!--[if mso]><table role="presentation" width="600" align="center" border="0" cellpadding="0" cellspacing="0"><tr><td><![endif]-->
<table role="presentation" width="100%" align="center" border="0" cellpadding="0" cellspacing="0" bgcolor="#171C20" style="width:100%;max-width:600px;table-layout:fixed;border-collapse:collapse;mso-table-lspace:0pt;mso-table-rspace:0pt;">
<tr><td bgcolor="#20262B" style="padding:22px 24px;border-top:3px solid #79D88B;">
 <table role="presentation" width="100%" border="0" cellpadding="0" cellspacing="0" style="width:100%;border-collapse:collapse;table-layout:fixed;">
 <tr><td width="56" valign="middle"><img src="cid:${reportLogo.contentId}" width="40" height="40" alt="Logo Winput LAN" style="display:block;width:40px;height:40px;border:0;color:#79D88B;font-size:10px;"></td>
 <td valign="middle" style="${FONT}"><p style="margin:0;color:#F1F5F2;font-size:18px;line-height:25px;font-weight:600;">Winput LAN</p><p style="margin:3px 0 0;color:#B8C7BE;font-size:13px;line-height:20px;">Relato de problema</p></td></tr>
 </table>
</td></tr>
<tr><td style="padding:26px 24px 28px;${WRAP}">
 <h1 style="margin:0 0 18px;color:#F1F5F2;${FONT}font-size:26px;line-height:34px;font-weight:600;${WRAP}">${content(report.title)}</h1>
 <p style="margin:0 0 4px;color:#B8C7BE;${FONT}font-size:13px;line-height:20px;">Protocolo</p>
 <p style="margin:0 0 12px;color:#79D88B;${FONT}font-size:14px;line-height:22px;${WRAP}">${content(receipt)}</p>
 <p style="margin:0;color:#B8C7BE;${FONT}font-size:13px;line-height:20px;">Capturado em ${escapeHtml(capturedTime(report.capturedUtc))}</p>
 ${section('O que aconteceu',content(report.description))}
 ${section('Como reproduzir',steps)}
 <h2 style="margin:28px 0 10px;color:#F1F5F2;${FONT}font-size:18px;line-height:25px;font-weight:600;">Diagnóstico técnico</h2>
 <table aria-label="Resumo do diagnóstico técnico" width="100%" border="0" cellpadding="0" cellspacing="0" bgcolor="#20262B" style="width:100%;table-layout:fixed;border-collapse:collapse;">
 ${summary.map(([label,value])=>diagnosticRow(label,value)).join('\n')}
 </table>
 <p style="margin:14px 0 0;color:#B8C7BE;${FONT}font-size:13px;line-height:20px;${WRAP}">O diagnóstico completo está no anexo<br><strong style="color:#DBE5DF;font-weight:600;">winputlan-diagnostic.json</strong>.</p>
</td></tr>
<tr><td bgcolor="#20262B" style="padding:20px 24px;">
 <p style="margin:0 0 6px;color:#DBE5DF;${FONT}font-size:13px;line-height:20px;font-weight:600;">Winput LAN</p>
 <p style="margin:0;color:#B8C7BE;${FONT}font-size:12px;line-height:19px;">Gerado pelo formulário Reportar problema.<br>Use o protocolo para identificar este relato.</p>
</td></tr>
</table>
<!--[if mso]></td></tr></table><![endif]-->
</td></tr></table>
</body></html>`;
 return {
  html,
  text:'Winput LAN — Relato de problema\nProtocolo: '+receipt+'\nCapturado em '+capturedTime(report.capturedUtc)+'\n\n'+report.title+'\n\nO que aconteceu:\n'+report.description+'\n\nComo reproduzir:\n'+(report.steps.trim()?report.steps:'Passos não informados.')+'\n\nDiagnóstico técnico:\n'+summary.map(([label,value])=>label+': '+value).join('\n')+'\n\nDiagnóstico completo no anexo winputlan-diagnostic.json.\n\nGerado pelo formulário Reportar problema.\nUse o protocolo para identificar este relato.',
  attachments:[{filename:'winputlan-diagnostic.json',type:'application/json',disposition:'attachment',content:new TextEncoder().encode(JSON.stringify(report.diagnostics,null,2))}, {...reportLogo,content:LOGO_BYTES}]
 };
}
