import {test} from 'node:test';
import assert from 'node:assert/strict';
import {readFile} from 'node:fs/promises';
import {buildReportEmail} from '../report-email.js';

function report() {
 return {id:'0123456789abcdef0123456789abcdef',capturedUtc:'2026-10-07T12:30:00Z',
  title:'Cursor parou ao trocar de máquina',description:'Ao mover o cursor para a direita, ele parou.\nA conexão continuou ativa.',
  steps:'1. Conectar as máquinas.\r\n2. Mover o cursor para a borda direita.',
  diagnostics:{metadata:{appVersion:'0.3.28',windowsVersion:'10.0.26100',architecture:'x64',elevated:false,outboundState:'Connected',inboundState:'Offline'},monitors:[{width:1920,height:1080}],events:[]}};
}

test('report email escapes visitor content, preserves line breaks and keeps diagnostic JSON intact',()=>{
 const r=report();r.title='<img src=x onerror="alert(1)">';
 r.description='Primeira linha & detalhes\r\n<script>alert("x")</script>\n\'última linha\'';
 r.steps='<a href="https://evil.test">abrir</a>';
 const message=buildReportEmail(r);
 assert.ok(!/<(?:script|a)\b|onerror="alert/.test(message.html));
 assert.ok(message.html.includes('&lt;img src=x onerror=&quot;alert(1)&quot;&gt;'));
 assert.ok(message.html.includes('Primeira linha &amp; detalhes<br>&lt;script&gt;alert(&quot;x&quot;)&lt;/script&gt;<br>&#39;última linha&#39;'));
 assert.ok(message.html.replaceAll('&#8203;','').includes('&lt;a href=&quot;https://evil.test&quot;&gt;abrir&lt;/a&gt;'));
 assert.ok(message.text.includes(r.description));
 assert.deepEqual(JSON.parse(new TextDecoder().decode(message.attachments[0].content)),r.diagnostics);
 assert.equal(message.attachments[0].disposition,'attachment');
});

test('email has readable empty/missing states and bounded layout even for long Unicode text',()=>{
 const r=report();r.title='W'.repeat(160);r.description='漢😀'.repeat(2000);r.steps=' \n';r.diagnostics.metadata={};
 const message=buildReportEmail(r);
 assert.ok(message.html.includes('max-width:600px;table-layout:fixed;'));
 assert.match(message.html,/\[if mso\]><table role="presentation" width="600" align="center"/);
 assert.ok(message.html.includes('&#8203;'));
 assert.ok(!message.html.includes('\uFFFD'));
 assert.ok(message.html.includes('Passos não informados.'));
 assert.ok(message.html.includes('Não informada'));
 assert.ok(!message.html.includes('undefined'));
 assert.ok(message.text.includes(r.title));
 assert.ok(message.text.includes(r.description));
 assert.ok(message.text.includes('07/10/2026 às 12:30:00 UTC'));
 assert.ok(Buffer.byteLength(message.html)<102400,'HTML stays below Gmail clipping size for worst-case prose');
});

test('email embeds the exact app logo PNG through the attachment Content-ID',async()=>{
 const message=buildReportEmail(report()),logo=message.attachments[1];
 assert.equal(logo.type,'image/png');assert.equal(logo.disposition,'inline');
 assert.ok(logo.content instanceof Uint8Array,'The binding must receive binary PNG content, not base64 text');
 assert.deepEqual([...logo.content.slice(0,8)],[137,80,78,71,13,10,26,10]);
 assert.ok(message.html.includes('src="cid:'+logo.contentId+'"'));
 assert.ok(!message.html.includes('src="http'));
 const icon=await readFile(new URL('../../../assets/brand/winput-lan.ico',import.meta.url));
 const png=Buffer.from(logo.content);
 let frame;
 for(let n=0;n<icon.readUInt16LE(4);n++) {
  const entry=6+16*n;
  if(icon[entry]===128) {const start=icon.readUInt32LE(entry+12);frame=icon.subarray(start,start+icon.readUInt32LE(entry+8));break;}
 }
 assert.deepEqual(png,frame);assert.equal(png.readUInt32BE(16),128);
});
