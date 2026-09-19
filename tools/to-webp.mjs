/**
 * 把 PNG / JPG / ICO 转成 WebP。
 *
 * WSL 里没有 cwebp / ImageMagick，这里用已经装好的 headless Chrome 做编码：
 * 把图片画进 canvas，再用 canvas.toDataURL('image/webp') 导出，属于浏览器原生编码器，
 * 不引入任何新依赖，也不联网。
 *
 *   node tools/to-webp.mjs <输入> <输出> [质量]
 */
import { mkdirSync, readFileSync, writeFileSync } from 'node:fs';
import { spawn } from 'node:child_process';
import { dirname, extname, resolve } from 'node:path';

const input = resolve(process.argv[2] ?? '');
const output = resolve(process.argv[3] ?? '');
const quality = Number(process.argv[4] ?? 0.95);
if (!process.argv[2] || !process.argv[3]) {
  console.error('用法: node tools/to-webp.mjs <输入> <输出> [质量]');
  process.exit(2);
}

const MIME = {
  '.png': 'image/png',
  '.jpg': 'image/jpeg',
  '.jpeg': 'image/jpeg',
  '.ico': 'image/x-icon',
  '.bmp': 'image/bmp',
  '.gif': 'image/gif',
  '.webp': 'image/webp',
};

const mime = MIME[extname(input).toLowerCase()];
if (!mime) throw new Error(`不支持的输入格式: ${input}`);

const port = 9223;
const sleep = (ms) => new Promise((done) => setTimeout(done, ms));

const chrome = spawn('google-chrome', [
  '--headless=new', '--disable-gpu', '--no-sandbox', '--no-first-run', '--disable-extensions',
  `--remote-debugging-port=${port}`, '--user-data-dir=/tmp/gfu-webp-profile', 'about:blank',
], { stdio: 'ignore' });

async function findTarget() {
  for (let i = 0; i < 60; i += 1) {
    try {
      const list = await fetch(`http://127.0.0.1:${port}/json/list`).then((r) => r.json());
      const page = list.find((item) => item.type === 'page');
      if (page?.webSocketDebuggerUrl) return page.webSocketDebuggerUrl;
    } catch { /* 浏览器还没起来 */ }
    await sleep(250);
  }
  throw new Error('连不上 Chrome 调试端口');
}

let socket;
try {
  socket = new WebSocket(await findTarget());
  await new Promise((done, fail) => {
    socket.addEventListener('open', done);
    socket.addEventListener('error', fail);
  });

  const base64 = readFileSync(input).toString('base64');
  const expression = `(async () => {
    const image = new Image();
    image.src = 'data:${mime};base64,${base64}';
    await image.decode();
    const canvas = document.createElement('canvas');
    canvas.width = image.naturalWidth;
    canvas.height = image.naturalHeight;
    const context = canvas.getContext('2d');
    context.drawImage(image, 0, 0);
    return JSON.stringify({
      data: canvas.toDataURL('image/webp', ${quality}),
      width: canvas.width,
      height: canvas.height,
    });
  })()`;

  const id = 1;
  const result = await new Promise((done, fail) => {
    const onMessage = (event) => {
      const message = JSON.parse(event.data);
      if (message.id !== id) return;
      socket.removeEventListener('message', onMessage);
      message.error ? fail(new Error(JSON.stringify(message.error))) : done(message.result);
    };
    socket.addEventListener('message', onMessage);
    socket.send(JSON.stringify({
      id,
      method: 'Runtime.evaluate',
      params: { expression, awaitPromise: true, returnByValue: true },
    }));
  });

  if (result.exceptionDetails) {
    throw new Error(`页面脚本报错: ${result.exceptionDetails.exception?.description ?? result.exceptionDetails.text}`);
  }

  const payload = JSON.parse(result.result.value);
  const data = payload.data.slice(payload.data.indexOf(',') + 1);
  mkdirSync(dirname(output), { recursive: true });
  writeFileSync(output, Buffer.from(data, 'base64'));
  console.log(`${input} → ${output} (${payload.width}x${payload.height}, ${Buffer.from(data, 'base64').length} 字节)`);
} finally {
  try { socket?.close(); } catch { /* ignore */ }
  chrome.kill('SIGTERM');
}
