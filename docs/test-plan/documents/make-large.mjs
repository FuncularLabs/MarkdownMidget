// Writes synthetic test files for docs/test-plan/TEST-PLAN-1.0.md. No dependencies; Node 18 or later.
//   node make-large.mjs [folder]      (default: %TEMP%\mdm-test-docs)
// Output: large-600kb.md and large-3mb.md (table- and list-heavy, long cells, a misspelling
// at the start and end of every paragraph), drop-01.md … drop-12.md, and picture.png.
// Don't commit the output.
import { mkdirSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import { tmpdir } from 'node:os';
import { deflateSync } from 'node:zlib';

const dir = process.argv[2] ?? join(process.env.TEMP ?? tmpdir(), 'mdm-test-docs');
mkdirSync(dir, { recursive: true });

const WORDS = ['apple', 'river', 'stone', 'cloud', 'garden', 'window', 'paper', 'silver', 'orange', 'market', 'yellow', 'bridge'];
const text = (seed, n) => Array.from({ length: n }, (_, k) => WORDS[Math.abs(seed * 7 + k * 5) % WORDS.length]).join(' ');

function section(i) {
  const rows = Array.from({ length: 12 }, (_, r) =>
    `| ${i}.${r} | ${text(i + r, 3)} | ${text(i * (r + 1), 30)} | [link](https://example.com/s${i}/r${r}) |`);
  const bullets = Array.from({ length: 6 }, (_, k) =>
    `- ${text(i + k, 8)}\n  - ${text(i + 2 * k, 12)}\n    - [ ] ${text(k + 1, 5)}`);
  const numbered = Array.from({ length: 4 }, (_, k) => `${k + 1}. ${text(i + 3 * k, 15)}`);
  return [
    `## Section ${i}`, '',
    `Qwzxvy opens this paragraph, ${text(i, 40)}, and qwzxvy closes it.`, '',
    '| ID | Name | Long description | Link |', '| --- | --- | --- | --- |', ...rows, '',
    ...bullets, '', ...numbered, '',
    '```text', text(i + 5, 20), '```', '',
  ].join('\n');
}

function build(name, bytes) {
  const parts = [`# ${name}\n\nSynthetic test document. Every link host is example.com.\n`];
  let size = Buffer.byteLength(parts[0]);
  for (let i = 1; size < bytes; i++) { const s = section(i); parts.push(s); size += Buffer.byteLength(s) + 1; }
  const out = parts.join('\n');
  writeFileSync(join(dir, name), out);
  console.log(`${join(dir, name)}  ${Math.round(Buffer.byteLength(out) / 1024)} KB`);
}

// A 160x40 solid PNG, for the picture drop.
function png(w, h) {
  const table = Array.from({ length: 256 }, (_, n) => { let c = n; for (let k = 0; k < 8; k++) c = c & 1 ? 0xedb88320 ^ (c >>> 1) : c >>> 1; return c >>> 0; });
  const crc = (buf) => { let c = 0xffffffff; for (const b of buf) c = table[(c ^ b) & 0xff] ^ (c >>> 8); return (c ^ 0xffffffff) >>> 0; };
  const chunk = (type, data) => {
    const len = Buffer.alloc(4), sum = Buffer.alloc(4), body = Buffer.concat([Buffer.from(type), data]);
    len.writeUInt32BE(data.length); sum.writeUInt32BE(crc(body)); return Buffer.concat([len, body, sum]);
  };
  const ihdr = Buffer.alloc(13); ihdr.writeUInt32BE(w, 0); ihdr.writeUInt32BE(h, 4); ihdr[8] = 8; ihdr[9] = 2;
  const row = Buffer.concat([Buffer.from([0]), Buffer.from(Array.from({ length: w }, () => [0x6a, 0x8f, 0xd0]).flat())]);
  return Buffer.concat([Buffer.from([137, 80, 78, 71, 13, 10, 26, 10]), chunk('IHDR', ihdr),
    chunk('IDAT', deflateSync(Buffer.concat(Array(h).fill(row)))), chunk('IEND', Buffer.alloc(0))]);
}

build('large-600kb.md', 600 * 1024);
build('large-3mb.md', 3 * 1024 * 1024);
for (let n = 1; n <= 12; n++) {
  const id = String(n).padStart(2, '0');
  writeFileSync(join(dir, `drop-${id}.md`), `# Drop test ${id}\n\nThis window should show drop-${id}.md.\n`);
}
writeFileSync(join(dir, 'picture.png'), png(160, 40));
console.log(`${join(dir, 'drop-01.md')} … drop-12.md, picture.png`);
