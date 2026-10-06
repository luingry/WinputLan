import {test} from 'node:test';
import assert from 'node:assert/strict';
import {readFile} from 'node:fs/promises';
import {Miniflare} from 'miniflare';
import worker,{acceptReport,deliver,readBody,validateReport} from '../worker.js';

const now=Date.parse('2026-10-06T22:00:00Z');
const secret='A'.repeat(43);
function report(id=crypto.randomUUID().replaceAll('-','')){return {schema:1,id,capturedUtc:new Date(now).toISOString(),title:'Mouse travou',description:'O cursor parou na troca pela borda.',steps:'Trocar pela direita',diagnostics:{metadata:{appVersion:'0.3.28',outboundState:'Connected',elevated:false,latencyP50Ms:12},monitors:[{x:0,y:0,width:1920,height:1080,primary:true}],events:[]}};}
function request(r,ip='192.0.2.1',token='valid-token'){return new Request('https://bugs.luingry.com.br/api/reports',{method:'POST',headers:{'Content-Type':'application/json','CF-Connecting-IP':ip},body:JSON.stringify({report:r,secret,token})});}
const verify = id => async()=>Response.json({success:true,hostname:'bugs.luingry.com.br',action:'bug_report',cdata:id});
test('public routes restrict host, origin and methods and isolate the challenge',async()=>{
 const env={REPORT_HOST:'bugs.luingry.com.br',TURNSTILE_SITEKEY:'public-sitekey'};
 assert.equal((await worker.fetch(new Request('http://bugs.luingry.com.br/challenge'),env)).status,404);
 assert.equal((await worker.fetch(new Request('https://evil.test/challenge'),env)).status,404);
 assert.equal((await worker.fetch(new Request('https://bugs.luingry.com.br/health',{headers:{Origin:'https://evil.test'}}),env)).status,403);
 assert.equal((await worker.fetch(new Request('https://bugs.luingry.com.br/api/reports'),env)).status,404);
 assert.equal((await worker.fetch(new Request('https://bugs.luingry.com.br/challenge?id=%3Cscript%3E'),env)).status,400);
 const page=await worker.fetch(new Request('https://bugs.luingry.com.br/challenge?id='+report().id),env);
 assert.equal(page.headers.get('Cache-Control'),'no-store');assert.ok(page.headers.get('Content-Security-Policy').includes("default-src 'none'"));
 assert.ok(!(await page.text()).includes('diagnostics'));assert.equal((await (await worker.fetch(new Request('https://bugs.luingry.com.br/health'),env)).json()).ready,false);
});
async function setup(){
 const mf=new Miniflare({modules:true,script:'export default {fetch(){return new Response("ok")}}',compatibilityDate:'2026-07-01',d1Databases:{DB:'report-test'}});
 const db=await mf.getD1Database('DB');const sql=await readFile(new URL('../migrations/0001.sql',import.meta.url),'utf8');
 // exec() splits on line boundaries. Prepare complete trigger statements individually.
 const stmts=sql.replace(/--[^\n]*/g,'').split(/(?=CREATE (?:TABLE|INDEX|TRIGGER))/).map(s=>s.trim()).filter(Boolean);
 try { await db.batch(stmts.map(s=>db.prepare(s))); await db.batch(stmts.map(s=>db.prepare(s))); } catch(e) { await mf.dispose(); throw e; }
 const mail=[];return {mf,db,mail,env:{DB:db,REPORT_HOST:'bugs.luingry.com.br',IP_HASH_SECRET:'test-secret-no-public-bypass',TURNSTILE_SECRET:'private',EMAIL_FROM:'winputlan-feedback@luingry.com.br',EMAIL_TO:'verified@example.com',EMAIL:{async send(m){mail.push(m);}}}};
}
test('stream and schema boundaries reject malicious diagnostics',async()=>{
 const r=report();r.diagnostics.metadata.password='secret';assert.throws(()=>validateReport(r));delete r.diagnostics.metadata.password;
 r.diagnostics.events=[{time:r.capturedUtc,origin:'local',destination:'remote',type:'Input.KeyDown',status:'typed secret'}];assert.throws(()=>validateReport(r));
 await assert.rejects(()=>readBody(new Request('https://test',{method:'POST',headers:{'Content-Type':'application/json'},body:' '.repeat(100000)})),e=>e.status===413);
 await assert.rejects(()=>readBody(new Request('https://test',{method:'POST',headers:{'Content-Type':'application/json','content-encoding':'gzip'},body:'{}'})),e=>e.status===415);
 const bytes=new TextEncoder().encode(' '.repeat(100000));let n=0;
 await assert.rejects(()=>readBody(new Request('https://test',{method:'POST',headers:{'Content-Type':'application/json'},duplex:'half',body:new ReadableStream({})}),1024,20),e=>e.status===408);
 await assert.rejects(()=>readBody(new Request('https://test',{method:'POST',headers:{'Content-Type':'application/json'},duplex:'half',body:new ReadableStream({pull(c){if(n>=bytes.length)c.close();else{c.enqueue(bytes.subarray(n,n+1000));n+=1000;}}})})),e=>e.status===413);
});
test('D1 commits report and outbox once; uncertain retries work without a new captcha',async()=>{
 const s=await setup();try{
  const r=report();const results=await Promise.all(Array.from({length:8},()=>acceptReport(request(r),s.env,verify(r.id),now)));
  for(const result of results)assert.equal((await result.json()).receipt,'WLR-'+r.id);
  assert.equal((await s.db.prepare('SELECT COUNT(*) AS n FROM reports').first()).n,1);
  assert.equal((await s.db.prepare('SELECT COUNT(*) AS n FROM outbox').first()).n,1);
  await acceptReport(request(r,'192.0.2.1',''),s.env,()=>{throw new Error('captcha must not be called');},now);
  const changed=structuredClone(r);changed.title='Outro conteúdo';await assert.rejects(()=>acceptReport(request(changed),s.env,verify(r.id),now),e=>e.status===409);
  await deliver(s.env,now);await deliver(s.env,now);assert.equal(s.mail.length,1);
  assert.equal(s.mail[0].to,'verified@example.com');assert.ok(s.mail[0].attachments[0].content.length>0);
 }finally{await s.mf.dispose();}
});
test('concurrent IP and global quotas are enforced atomically',async()=>{
 const s=await setup();try{
  const requests=Array.from({length:16},()=>{const r=report();return acceptReport(request(r),s.env,verify(r.id),now);});
  const outcomes=await Promise.allSettled(requests);assert.equal(outcomes.filter(x=>x.status==='fulfilled').length,3);
  assert.equal((await s.db.prepare('SELECT COUNT(*) AS n FROM outbox').first()).n,3);
  const burst=Array.from({length:108},(_,i)=>{const r=report();return acceptReport(request(r,'198.51.100.'+(i+1)),s.env,verify(r.id),now);});
  await Promise.allSettled(burst);assert.equal((await s.db.prepare('SELECT COUNT(*) AS n FROM reports').first()).n,50);
  assert.equal((await s.db.prepare('SELECT COUNT(*) AS n FROM outbox').first()).n,50);
 }finally{await s.mf.dispose();}
});
test('Turnstile fails closed and a failed outbox transaction leaves no report',async()=>{
 const s=await setup();try{
  const r=report();await assert.rejects(()=>acceptReport(request(r,'192.0.2.1',''),s.env,()=>{throw new Error('no token');},now),e=>e.status===403);
  for(const v of [{success:false},{success:true,hostname:'evil.test',action:'bug_report',cdata:r.id},{success:true,hostname:'bugs.luingry.com.br',action:'other',cdata:r.id},{success:true,hostname:'bugs.luingry.com.br',action:'bug_report',cdata:'wrong'}])await assert.rejects(()=>acceptReport(request(r),s.env,async()=>Response.json(v),now),e=>e.status===403);
  await assert.rejects(()=>acceptReport(request(r),s.env,async()=>{throw new Error('offline');},now),e=>e.status===503);
  await s.db.prepare("CREATE TRIGGER break_outbox BEFORE INSERT ON outbox BEGIN SELECT RAISE(ABORT,'test failure'); END;").run();
  await assert.rejects(()=>acceptReport(request(r),s.env,verify(r.id),now));assert.equal((await s.db.prepare('SELECT COUNT(*) AS n FROM reports').first()).n,0);
 }finally{await s.mf.dispose();}
});
test('mail failure is durable, concurrent cron leases prevent duplicate notification',async()=>{
 const s=await setup();try{
  const r=report();await acceptReport(request(r),s.env,verify(r.id),now);
  s.env.EMAIL.send=async()=>{throw new Error('provider down');};await deliver(s.env,now);
  let row=await s.db.prepare('SELECT * FROM outbox').first();assert.equal(row.sent,null);assert.equal(row.attempts,1);assert.equal(row.failed,0);
  s.env.EMAIL.send=async m=>{s.mail.push(m);await new Promise(r=>setTimeout(r,25));};
  await Promise.all([deliver(s.env,now+121000),deliver(s.env,now+121000)]);assert.equal(s.mail.length,1);
  row=await s.db.prepare('SELECT * FROM outbox').first();assert.ok(row.sent);
 }finally{await s.mf.dispose();}
});
