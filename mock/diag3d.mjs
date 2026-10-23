import { spawn } from 'node:child_process';
import { mkdtempSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import WebSocket from 'ws';
const sleep=(ms)=>new Promise(r=>setTimeout(r,ms));
const profile=mkdtempSync(join(tmpdir(),'diag-'));
const ch=spawn('C:/Program Files/Google/Chrome/Application/chrome.exe',['--headless=new','--no-proxy-server','--enable-unsafe-swiftshader',`--user-data-dir=${profile}`,'--remote-debugging-port=9888','about:blank'],{stdio:'ignore'});
let url; for(let i=0;i<60;i++){try{const l=await(await fetch('http://127.0.0.1:9888/json/list')).json();const p=l.find(t=>t.type==='page');if(p){url=p.webSocketDebuggerUrl;break}}catch{} await sleep(200);}
const ws=new WebSocket(url); await new Promise(r=>ws.on('open',r));
let id=0; const pend=new Map();
ws.on('message',m=>{const d=JSON.parse(m);if(d.id&&pend.has(d.id)){pend.get(d.id)(d.result);pend.delete(d.id)}});
const send=(method,params={})=>new Promise(r=>{const i=++id;pend.set(i,r);ws.send(JSON.stringify({id:i,method,params}))});
const ev=async(e)=>{const r=await send('Runtime.evaluate',{expression:e,returnByValue:true,awaitPromise:true});return r.exceptionDetails?{__err:r.exceptionDetails.text,detail:r.exceptionDetails.exception?.description}:r.result.value};
await send('Runtime.enable');await send('Page.enable');
const logs=[];
ws.on('message',m=>{const d=JSON.parse(m);
  if(d.method==='Runtime.consoleAPICalled') logs.push(d.params.type+': '+(d.params.args||[]).map(a=>a.value??a.description).join(' '));
  if(d.method==='Log.entryAdded') logs.push('LOG '+d.params.entry.level+': '+d.params.entry.text);
});
await send('Log.enable');
await send('Page.navigate',{url:'http://127.0.0.1:5293/'});
await sleep(2500);
console.log('importmap el:', await ev(`(()=>{const e=document.querySelector('script[type=importmap]');return e?e.textContent.length:'none'})()`));
console.log('logs:', logs.slice(0,6));
console.log(await ev(`(async()=>{
  try{
    const m = await import('three/webgpu');
    return { ok:true, type: typeof m, keys: Object.keys(m).slice(0,6), hasRenderer: typeof m.WebGPURenderer };
  }catch(e){ return { ok:false, err:String(e) }; }
})()`));
ws.close();ch.kill();process.exit(0);
