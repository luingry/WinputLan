import {buildReportEmail} from './report-email.js';

const MAX_BODY = 96 * 1024;
const STATES = ['Offline','Connecting','Pairing','Connected','Reconnecting','Faulted'];
const TYPES = ['Session','Config','Hooks','Transport','Access','Listener','Target','Edge','Input','Pairing','Startup','Update','Atalhos','Input.MouseMove','Input.MouseDelta','Input.MouseWheel','Input.MouseDown','Input.MouseUp','Input.KeyDown','Input.KeyUp','Input.ReleaseAll','Input.Focus'];
const STATUS = /^(ready(-elevated)?|pin-reset-safe|armed-controller-only|unavailable|connecting|failed|user-disconnected|reconnecting|retry-pending|controller-ready|target-ready|blocked-unpaired|selected(-edge [0-9]{1,3}%)?|restored(-edge [0-9]{1,3}%)?|locked-by-controller|unlocked|on|off|auto-accepted|auto-accept-failed|approval-requested|code-renewed(-trust-cleared)?|code-renewed-trust-cleared|request-cancelled|resume-requested|trust-rejected|auto-accepted|pin-save-failed|controller-active|blocked-by-windows|dropped-sink|sent|received|received-unfocused|dropped-unfocused|dropped-disconnected|task|run-key|salvos|no-update|postponed|validated|error|Offline|Connecting|Pairing|Connected|Reconnecting|Faulted|touched [0-9]{1,3}%|restored-edge [0-9]{1,3}%|redacted)$/;
const metadataKeys = ['appVersion','windowsVersion','clrVersion','architecture','networkType','elevated','startWithWindows','continueInBackground','runElevated','autoAcceptKnown','edgeSwitchEnabled','localEdge','remoteEdge','localHotkey','remoteHotkey','outboundState','inboundState','remoteActive','inboundFocused','uptimeSeconds','processMemoryMb','dpiScaleX','dpiScaleY','latencySamples','latencyP50Ms','latencyP95Ms','latencyMaxMs'];
export class RequestError extends Error { constructor(status, message) { super(message); this.status=status; } }
const bad = () => { throw new RequestError(400,'Relato inválido.'); };
function keys(value, allowed) { if(!value || typeof value!=='object' || Array.isArray(value) || Object.keys(value).some(k=>!allowed.includes(k))) bad(); }
function str(value,min,max) { if(typeof value!=='string'||value.length<min||value.length>max||/[\u0000-\u0008\u000b\u000c\u000e-\u001f\u007f]/.test(value)) bad(); return value; }
export function validateReport(r) {
 keys(r,['schema','id','capturedUtc','title','description','steps','diagnostics']);
 if(r.schema!==1||!/^\w{32}$/.test(r.id)||! /^[a-f0-9]{32}$/.test(r.id)) bad();
 str(r.capturedUtc,20,40); if(!Number.isFinite(Date.parse(r.capturedUtc))) bad();
 str(r.title,3,160); str(r.description,10,6000); str(r.steps,0,4000);
 const d=r.diagnostics; keys(d,['metadata','monitors','events']); keys(d.metadata,metadataKeys);
 for(const [k,v] of Object.entries(d.metadata)) {
  if(['elevated','startWithWindows','continueInBackground','runElevated','autoAcceptKnown','edgeSwitchEnabled','remoteActive','inboundFocused'].includes(k)) { if(typeof v!=='boolean') bad(); }
  else if(['uptimeSeconds','processMemoryMb','dpiScaleX','dpiScaleY','latencySamples','latencyP50Ms','latencyP95Ms','latencyMaxMs'].includes(k)) { if(typeof v!=='number'||!Number.isFinite(v)||v<0||v>1e9) bad(); }
  else { str(v,0,80); if(!/^[a-zA-Z0-9.+ _-]*$/.test(v)) bad(); }
  if(['outboundState','inboundState'].includes(k)&&!STATES.includes(v)) bad();
 }
 if(!Array.isArray(d.monitors)||d.monitors.length>16||!Array.isArray(d.events)||d.events.length>500) bad();
 for(const m of d.monitors) { keys(m,['x','y','width','height','primary']); if(typeof m.primary!=='boolean'||['x','y','width','height'].some(k=>!Number.isInteger(m[k])||Math.abs(m[k])>100000)||m.width<1||m.height<1) bad(); }
 for(const e of d.events) { keys(e,['time','origin','destination','type','status']); str(e.time,20,40); if(!Number.isFinite(Date.parse(e.time))||!['local','remote','system'].includes(e.origin)||!['local','remote','system'].includes(e.destination)||!TYPES.includes(e.type)||typeof e.status!=='string'||!STATUS.test(e.status)) bad(); }
 return r;
}
function canonical(value) { if(Array.isArray(value))return '['+value.map(canonical).join(',')+']'; if(value&&typeof value==='object')return '{'+Object.keys(value).sort().map(k=>JSON.stringify(k)+':'+canonical(value[k])).join(',')+'}'; return JSON.stringify(value); }
async function hash(text) { return [...new Uint8Array(await crypto.subtle.digest('SHA-256',new TextEncoder().encode(text)))].map(x=>x.toString(16).padStart(2,'0')).join(''); }
function sameHash(a,b) {
 const left=new TextEncoder().encode(a),right=new TextEncoder().encode(b);
 if(typeof crypto.subtle.timingSafeEqual==='function')return crypto.subtle.timingSafeEqual(left,right);
 // Node test runners lack the Workers extension; fixed-length hashes still compare without early exit.
 let different=left.length^right.length;for(let i=0;i<64;i++)different|=(left[i]||0)^(right[i]||0);return different===0;
}
async function ipKey(ip,secret,day) { const k=await crypto.subtle.importKey('raw',new TextEncoder().encode(secret),{name:'HMAC',hash:'SHA-256'},false,['sign']); return [...new Uint8Array(await crypto.subtle.sign('HMAC',k,new TextEncoder().encode(day+':'+ip)))].map(x=>x.toString(16).padStart(2,'0')).join(''); }
export async function readBody(request,limit=MAX_BODY,timeoutMs=10000) {
 if(request.headers.has('content-encoding')||!/^application\/json(?:;|$)/i.test(request.headers.get('content-type')||'')) throw new RequestError(415,'Formato não permitido.');
 if(Number(request.headers.get('content-length'))>limit)throw new RequestError(413,'Relato muito grande.');
 if(!request.body)bad(); const reader=request.body.getReader(); let total=0; const chunks=[];
 let timer; const deadline=new Promise((_,reject)=>{timer=setTimeout(()=>reject(new RequestError(408,'O envio demorou demais. Tente novamente.')),timeoutMs);});
 try { for(;;){const {done,value}=await Promise.race([reader.read(),deadline]);if(done)break;total+=value.length;if(total>limit)throw new RequestError(413,'Relato muito grande.');chunks.push(value);} }
 finally { clearTimeout(timer); void reader.cancel().catch(()=>{}); }
 const bytes=new Uint8Array(total);let offset=0;for(const chunk of chunks){bytes.set(chunk,offset);offset+=chunk.length;}
 try{return JSON.parse(new TextDecoder('utf-8',{fatal:true}).decode(bytes));}catch{bad();}
}
function headers(extra={}) { return {'Cache-Control':'no-store','X-Content-Type-Options':'nosniff','Referrer-Policy':'no-referrer','X-Frame-Options':'DENY',...extra}; }
function json(body,status=200){return new Response(JSON.stringify(body),{status,headers:headers({'Content-Type':'application/json;charset=utf-8',...(status===429?{'Retry-After':'3600'}:{})})});}
async function budget(db,key,limit,expires) { const row=await db.prepare('INSERT INTO budgets(key,count,expires) VALUES(?,1,?) ON CONFLICT(key) DO UPDATE SET count=count+1 WHERE count < ? RETURNING count').bind(key,expires,limit).first();return !!row; }
async function stored(db,id,secretHash,contentHash) {
 const row=await db.prepare('SELECT secret_hash,content_hash FROM reports WHERE id=?').bind(id).first();
 if(!row)return null; if(!sameHash(row.secret_hash,secretHash)||row.content_hash!==contentHash)throw new RequestError(409,'Este relato já foi enviado com outro conteúdo.');return json({receipt:'WLR-'+id,accepted:true});
}
export async function acceptReport(request,env,fetchImpl=fetch,now=Date.now()) {
 const body=await readBody(request); keys(body,['report','secret','token']); validateReport(body.report);
 if(typeof body.secret!=='string'||!/^[A-Za-z0-9_-]{43}$/.test(body.secret))bad();
 const secretHash=await hash(body.secret), content=canonical(body.report), contentHash=await hash(content);
 const day=Math.floor(now/86400000), hour=Math.floor(now/3600000), second=Math.floor(now/1000);
 const ip=request.headers.get('CF-Connecting-IP'); if(!ip)throw new RequestError(503,'Serviço indisponível.');
 const ipHash=await ipKey(ip,env.IP_HASH_SECRET,day);
 if(env.ABUSE_RATE && !(await env.ABUSE_RATE.limit({key:ipHash})).success)throw new RequestError(429,'Muitas tentativas. Tente mais tarde.');
 if(!await budget(env.DB,'attempt:'+ipHash+':'+Math.floor(now/60000),20,second+120))throw new RequestError(429,'Muitas tentativas. Tente mais tarde.');
 const previous=await stored(env.DB,body.report.id,secretHash,contentHash);if(previous)return previous;
 if(typeof body.token!=='string'||body.token.length>2048)bad();
 if(body.token.length<1)throw new RequestError(403,'Refaça a verificação de segurança.');
 // Stored receipts above remain confirmable without CAPTCHA or verification budget.
 // Invalid tokens must not let one IP consume the shared daily capacity.
 if(!await budget(env.DB,'verify-ip:'+ipHash,20,second+172800))throw new RequestError(429,'Limite diário de verificações atingido. Tente mais tarde.');
 if(!await budget(env.DB,'verify:'+day,10000,second+172800))throw new RequestError(429,'Limite diário de tentativas atingido.');
 let verification;
 try { const res=await fetchImpl('https://challenges.cloudflare.com/turnstile/v0/siteverify',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({secret:env.TURNSTILE_SECRET,response:body.token,remoteip:ip}),signal:AbortSignal.timeout(8000)}); if(!res.ok)throw new Error();verification=await res.json(); }
 catch {throw new RequestError(503,'Não foi possível verificar agora. Tente novamente.');}
 if(verification.success!==true||verification.hostname!==env.REPORT_HOST||verification.action!=='bug_report'||verification.cdata!==body.report.id)throw new RequestError(403,'Refaça a verificação de segurança.');
 try { await env.DB.prepare('INSERT INTO reports(id,secret_hash,content_hash,ip_key,hour,day,created,body) VALUES(?,?,?,?,?,?,?,?) ON CONFLICT(id) DO NOTHING').bind(body.report.id,secretHash,contentHash,ipHash,hour,day,second,content).run(); }
 catch(e) { if(String(e.message).includes('REPORT_QUOTA'))throw new RequestError(429,'Limite de relatos atingido. Tente mais tarde.');throw e; }
 return await stored(env.DB,body.report.id,secretHash,contentHash);
}
export async function deliver(env,now=Date.now()) {
 const second=Math.floor(now/1000),day=Math.floor(now/86400000);
 const pending=await env.DB.prepare('SELECT report_id FROM outbox WHERE sent IS NULL AND failed=0 AND next_attempt<=? AND lease_until<=? ORDER BY next_attempt LIMIT 10').bind(second,second).all();
 for(const item of pending.results) {
  const claim=await env.DB.prepare('UPDATE outbox SET lease_until=?,attempts=attempts+1 WHERE report_id=? AND sent IS NULL AND failed=0 AND lease_until<=? AND next_attempt<=? RETURNING attempts').bind(second+180,item.report_id,second,second).first();if(!claim)continue;
  if(!await budget(env.DB,'email:'+day,100,second+172800)) {await env.DB.prepare('UPDATE outbox SET lease_until=0,next_attempt=?,attempts=attempts-1 WHERE report_id=?').bind((day+1)*86400,item.report_id).run();continue;}
  try {
   const row=await env.DB.prepare('SELECT body FROM reports WHERE id=?').bind(item.report_id).first(); if(!row)throw new Error(); const report=JSON.parse(row.body);
   // Fixed headers/recipient; visitor text is escaped by the email template.
   await env.EMAIL.send({from:env.EMAIL_FROM,to:env.EMAIL_TO,subject:'[Winput LAN] Report WLR-'+item.report_id,
    ...buildReportEmail(report)});
   await env.DB.prepare('UPDATE outbox SET sent=?,lease_until=0 WHERE report_id=?').bind(second,item.report_id).run();
  } catch {
   await env.DB.prepare('UPDATE outbox SET lease_until=0,next_attempt=?,failed=? WHERE report_id=?').bind(second+Math.min(86400,60*2**claim.attempts),claim.attempts>=8?1:0,item.report_id).run();
  }
 }
 await env.DB.batch([env.DB.prepare('DELETE FROM outbox WHERE report_id IN(SELECT id FROM reports WHERE created<?)').bind(second-90*86400),env.DB.prepare('DELETE FROM reports WHERE created<?').bind(second-90*86400),env.DB.prepare('DELETE FROM budgets WHERE expires<?').bind(second)]);
}
function challenge(url,env) {
 const id=url.searchParams.get('id'); if(!/^[a-f0-9]{32}$/.test(id||''))return json({error:'Identificador inválido.'},400);
 const nonce=crypto.randomUUID().replaceAll('-','');
 const page='<!doctype html><html lang="pt-BR"><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1"><style nonce="'+nonce+'">body{margin:0;background:#171c20;color:#dbe5df;font:13px Segoe UI}</style><div id="challenge"></div><script nonce="'+nonce+'">function send(v){if(window.chrome&&window.chrome.webview)window.chrome.webview.postMessage(v);}function ready(){turnstile.render("#challenge",{sitekey:'+JSON.stringify(env.TURNSTILE_SITEKEY)+',action:"bug_report",cData:'+JSON.stringify(id)+',theme:"dark",appearance:"interaction-only","refresh-expired":"auto","refresh-timeout":"auto","before-interactive-callback":function(){send({type:"interactive"});},"after-interactive-callback":function(){send({type:"noninteractive"});},"timeout-callback":function(){send({type:"expired"});},callback:function(token){send({type:"token",token:token});},"expired-callback":function(){send({type:"expired"});},"error-callback":function(){send({type:"error"});}});}</script><script nonce="'+nonce+'" src="https://challenges.cloudflare.com/turnstile/v0/api.js?onload=ready&amp;render=explicit" defer></script></html>';
 return new Response(page,{headers:headers({'Content-Type':'text/html;charset=utf-8','Content-Security-Policy':"default-src 'none'; script-src 'nonce-"+nonce+"' https://challenges.cloudflare.com; style-src 'nonce-"+nonce+"'; frame-src https://challenges.cloudflare.com; connect-src https://challenges.cloudflare.com; base-uri 'none'; form-action 'none'; frame-ancestors 'none'"})});
}
export default {
 async fetch(request,env) {
  try {
   const url=new URL(request.url);if(url.protocol!=='https:'||url.hostname!==env.REPORT_HOST)return json({error:'Host inválido.'},404);
   if(request.headers.has('Origin')&&request.headers.get('Origin')!==url.origin)return json({error:'Origem inválida.'},403);
   if(url.pathname==='/health'&&request.method==='GET')return json({ready:!!(env.DB&&env.EMAIL&&env.TURNSTILE_SECRET&&env.IP_HASH_SECRET)});
   if(url.pathname==='/challenge'&&request.method==='GET')return challenge(url,env);
   if(url.pathname==='/api/reports'&&request.method==='POST')return await acceptReport(request,env);
   return json({error:'Recurso não encontrado.'},404);
  }catch(e){return json({error:e instanceof RequestError?e.message:'Serviço indisponível. Tente novamente nesta janela.'},e instanceof RequestError?e.status:503);}
 },
 async scheduled(_controller,env,ctx) {ctx.waitUntil(deliver(env));}
};
