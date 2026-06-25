// Bundles the Milkdown editor surface into the WPF app's wwwroot.
// esbuild collects the CSS imported from JS into a sibling .css bundle.
import esbuild from 'esbuild';
import { mkdirSync, readFileSync, writeFileSync } from 'node:fs';
import { createHash } from 'node:crypto';
import { fileURLToPath } from 'node:url';
import { dirname, resolve } from 'node:path';

const here = dirname(fileURLToPath(import.meta.url));
const outdir = resolve(here, '../src/MarkdownMidget/wwwroot');

mkdirSync(outdir, { recursive: true });

const options = {
  entryPoints: { 'editor.bundle': resolve(here, 'src/main.js') },
  bundle: true,
  format: 'iife',
  target: ['chrome120'],
  outdir,
  loader: { '.ttf': 'dataurl', '.woff': 'dataurl', '.woff2': 'dataurl', '.svg': 'dataurl' },
  logLevel: 'info',
  sourcemap: true,
  minify: true,
};

// Copy index.html, stamping the bundle references with a content hash so the
// WebView2 never serves a stale cached bundle after a rebuild.
function copyStatic() {
  const js = readFileSync(resolve(outdir, 'editor.bundle.js'));
  const css = readFileSync(resolve(outdir, 'editor.bundle.css'));
  const v = createHash('sha256').update(js).update(css).digest('hex').slice(0, 12);
  const html = readFileSync(resolve(here, 'index.html'), 'utf8')
    .replace('editor.bundle.css', `editor.bundle.css?v=${v}`)
    .replace('editor.bundle.js', `editor.bundle.js?v=${v}`);
  writeFileSync(resolve(outdir, 'index.html'), html);
}

const watch = process.argv.includes('--watch');

if (watch) {
  const ctx = await esbuild.context(options);
  await ctx.rebuild();
  copyStatic();
  await ctx.watch();
  console.log('watching editor-src for changes…');
} else {
  await esbuild.build(options);
  copyStatic();
  console.log('editor bundle written to', outdir);
}
