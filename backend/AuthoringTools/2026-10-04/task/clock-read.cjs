const net = require('net');
const pipe = '\\\\.\\pipe\\ExpanseFoundations.Clock.View.v1.' + process.env.USERNAME;
const s = net.connect(pipe);
let data = Buffer.alloc(0);
s.setTimeout(2000, () => s.destroy(Error('Pipe timeout')));
s.on('error', e => { console.error(e.message); process.exitCode = 1; });
s.on('connect', () => {
  const body = Buffer.from(JSON.stringify({protocolVersion:1,messageType:'getSnapshot'}));
  const head = Buffer.alloc(4); head.writeUInt32LE(body.length);
  s.write(Buffer.concat([head,body]));
});
s.on('data', chunk => {
  data = Buffer.concat([data,chunk]);
  if (data.length < 4 || data.length < data.readUInt32LE() + 4) return;
  const frame = JSON.parse(data.subarray(4,4+data.readUInt32LE()));
  console.log(JSON.stringify({status:frame.status,sample:frame.sample && {activeWorld:frame.sample.activeWorld,utSeconds:frame.sample.utSeconds,formattedDate:frame.sample.formattedDate,sequence:frame.sample.sequence,sessionId:frame.sample.sessionId,loadEpoch:frame.sample.loadEpoch}}));
  s.end();
});
