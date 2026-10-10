// Static server for the repo: /promo/... and /img/... (docs/icon.png, docs/images/...).
const http = require('node:http');
const fs = require('node:fs');
const path = require('node:path');
const ROOT = path.resolve(__dirname, '..');
const TYPES = { '.html': 'text/html', '.js': 'text/javascript', '.css': 'text/css', '.json': 'application/json', '.png': 'image/png', '.wav': 'audio/wav', '.mp4': 'video/mp4' };
function resolve(url) {
  const p = decodeURIComponent(url.split('?')[0]);
  if (p === '/img/icon.png') return path.join(ROOT, 'docs/icon.png');
  if (p.startsWith('/img/')) return path.join(ROOT, 'docs/images', p.slice(5));
  return path.join(ROOT, p);
}
function start(port = 0) {
  return new Promise((res) => {
    const srv = http.createServer((req, rsp) => {
      const f = resolve(req.url);
      if (!f.startsWith(ROOT) || !fs.existsSync(f) || fs.statSync(f).isDirectory()) { rsp.writeHead(404); return rsp.end(); }
      rsp.writeHead(200, { 'Content-Type': TYPES[path.extname(f)] || 'application/octet-stream', 'Cache-Control': 'no-store' });
      fs.createReadStream(f).pipe(rsp);
    });
    srv.listen(port, '127.0.0.1', () => res(srv));
  });
}
module.exports = { start };
if (require.main === module) start(+process.env.PORT || 8123).then((s) => console.log('http://127.0.0.1:' + s.address().port + '/promo/src/index.html'));
