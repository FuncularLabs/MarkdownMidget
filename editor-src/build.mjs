// Bundles the Milkdown editor surface into the WPF app's wwwroot.
// esbuild collects the CSS imported from JS into a sibling .css bundle.
import esbuild from 'esbuild';
import { cpSync, mkdirSync } from 'node:fs';
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

function copyStatic() {
  cpSync(resolve(here, 'index.html'), resolve(outdir, 'index.html'));
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
