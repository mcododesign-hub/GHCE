import WebSocket from 'ws';
const ws = new WebSocket('ws://127.0.0.1:8788/ghce');
const seen = [];
ws.on('open', () => {
  ws.send(JSON.stringify({ v:'0.1', t:'hello', client:'grab', caps:{} }));
  console.log('connected');
});
ws.on('message', (d) => {
  const m = JSON.parse(d.toString());
  if (m.t === 'signal') {
    seen.push(m);
    if (seen.length === 1) console.log('first signal:', JSON.stringify(m));
  }
});
setTimeout(() => {
  console.log('signal messages:', seen.length);
  if (seen.length) {
    const m = seen[seen.length - 1];
    const ok = m.v === '0.1' && m.id === 'a1b2c3d4' && m.kind === 'noise1' && m.seed === 7 && m.rate === 0.6 && Array.isArray(m.args);
    console.log('payload ok:', ok, JSON.stringify(m));
  }
  ws.close();
  process.exit(0);
}, 3500);
