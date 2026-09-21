// When diagrams are redrawn: on a change of mermaid theme or of the document's size,
// and at no other time.
//
// The real editor in jsdom, with mermaid's two entry points replaced by recorders:
// jsdom has no layout, so a real render can only fail, and what is under test here is
// the plugin's bookkeeping around it — the cache, the epoch and the decoration keys —
// not mermaid's drawing. The stand-in draws an <svg> that says which size mermaid was
// initialised with when it was asked to draw, which is the thing that went wrong: a
// drawing made at one size, shown at another.
//
// The tests share one editor and run in order; each says what state it leaves.
import test from 'node:test';
import assert from 'node:assert/strict';
import { mountEditor } from './jsdom-editor.mjs';

// Mounted empty, so nothing renders before the recorders are in place.
const ed = await mountEditor('');
const { default: mermaid } = await import('mermaid');
const { setMermaidTheme } = await import('../src/mermaid.js');

const inits = [];
const renders = [];
let hold = null;   // when set, render() waits for the test to release it
mermaid.initialize = (config) => { inits.push(config); };
mermaid.render = async (id, source) => {
  const size = inits.at(-1)?.themeVariables?.fontSize;
  const call = { source, size };
  renders.push(call);
  if (hold) await new Promise((release) => { call.release = release; hold.push(call); });
  return { svg: `<svg data-size="${size}"></svg>` };
};

const settle = async () => { for (let i = 0; i < 5; i++) await new Promise((r) => setTimeout(r, 0)); };
const frames = () => [...ed.window.document.querySelectorAll('.mdm-mermaid')];
const sizes = () => frames().map((f) => f.querySelector('svg')?.getAttribute('data-size') ?? f.textContent);
const view = () => ed.view();

const DIAGRAM = '```mermaid\ngraph LR\n    A[Aerial] --> B{Sparks}\n```';

test('a document with a diagram draws it once, at the size mermaid was given', async () => {
  ed.roundTrip(`Some prose.\n\n${DIAGRAM}\n`);
  await settle();
  assert.equal(renders.length, 1);
  assert.equal(inits.length, 1);
  assert.deepEqual(inits[0].themeVariables, { fontSize: '16px' });
  assert.deepEqual(sizes(), ['16px']);
});

test('the same theme at the same size redraws nothing', async () => {
  // readThemeBack runs on every setTheme, including one that changes nothing mermaid
  // cares about — a palette with the same mermaid theme and the same size.
  const frame = frames()[0];
  assert.equal(setMermaidTheme(view(), 'default', 16), false);
  assert.equal(setMermaidTheme(view(), 'default', '16px'), false);
  await settle();
  assert.equal(renders.length, 1);
  assert.equal(inits.length, 1);
  assert.equal(frames()[0], frame, 'the diagram\'s DOM was rebuilt');
});

test('typing elsewhere in the document redraws nothing', async () => {
  const frame = frames()[0];
  const v = view();
  v.dispatch(v.state.tr.insertText('More ', 1));
  v.dispatch(v.state.tr.insertText('text ', 1));
  await settle();
  assert.equal(renders.length, 1);
  assert.equal(frames()[0], frame, 'the diagram\'s DOM was rebuilt by a keystroke');
});

test('a new size redraws every diagram at that size, and then settles', async () => {
  assert.equal(setMermaidTheme(view(), 'default', 32), true);
  await settle();
  assert.equal(inits.at(-1).themeVariables.fontSize, '32px');
  assert.equal(renders.length, 2);
  assert.deepEqual(sizes(), ['32px'], 'the 16px drawing is still on screen');

  // A second copy of the same diagram comes out of the cache, at the new size.
  ed.roundTrip(`Some prose.\n\n${DIAGRAM}\n\n${DIAGRAM}\n`);
  await settle();
  assert.deepEqual(sizes(), ['32px', '32px']);
  assert.equal(renders.length, 2, 'an identical diagram was drawn again instead of served');

  assert.equal(setMermaidTheme(view(), 'default', '32px'), false);
  await settle();
  assert.equal(renders.length, 2);
});

test('a theme change at the same size still redraws', async () => {
  // Two copies on screen now, and both start drawing before either has finished, so
  // neither can be served the other's: one render each, as before this change.
  const before = renders.length;
  assert.equal(setMermaidTheme(view(), 'dark', 32), true);
  await settle();
  assert.equal(inits.at(-1).theme, 'dark');
  assert.equal(inits.at(-1).themeVariables.fontSize, '32px', 'a theme switch dropped the size');
  assert.equal(renders.length, before + 2);
  assert.deepEqual(sizes(), ['32px', '32px']);
});

test('a drawing that finishes after the size moved on is thrown away, not shown or cached', async () => {
  // Two changes in quick succession, the first drawing slower than the second: the
  // retry/replay case. Whatever finishes last, what is on screen and what the cache
  // will serve are the size the document has NOW.
  ed.roundTrip(`Some prose.\n\n${DIAGRAM}\n`);
  await settle();
  hold = [];
  setMermaidTheme(view(), 'dark', 24);
  await settle();
  setMermaidTheme(view(), 'dark', 40);
  await settle();
  const [at24, at40] = hold;
  assert.deepEqual([at24.size, at40.size], ['24px', '40px']);

  at40.release();
  await settle();
  at24.release();
  await settle();
  hold = null;
  assert.deepEqual(sizes(), ['40px'], 'the late 24px drawing replaced the current one');

  // And it was not cached as the current look's drawing: a new copy of the diagram is
  // served the 40px one without a render.
  const before = renders.length;
  ed.roundTrip(`Some prose.\n\n${DIAGRAM}\n\n${DIAGRAM}\n`);
  await settle();
  assert.deepEqual(sizes(), ['40px', '40px']);
  assert.equal(renders.length, before);
});
