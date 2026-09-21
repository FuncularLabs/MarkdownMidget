// When diagrams are redrawn, and under what: on a change of mermaid theme or of the
// document's size, at no other time, and each under the look it was asked for.
//
// The real editor in jsdom, with mermaid's render replaced by a stand-in and its initialize
// recorded on the way through: jsdom has no layout, so a real render can only fail, and what
// is under test here is the plugin's bookkeeping around it — the queue, the cache, the epoch
// and the decoration keys — not mermaid's drawing. The stand-in behaves as mermaid 11 does
// where it matters: renders run one at a time, and each reads mermaid's config when it
// STARTS, not when it was asked for. It draws an <svg> that says which size that config
// held, which is the thing that goes wrong: a drawing made at one size, shown at another.
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
let hold = null;   // when set, a render waits, once started, for the test to release it
const realInitialize = mermaid.initialize.bind(mermaid);
mermaid.initialize = (config) => { inits.push(config); realInitialize(config); };
let mermaidQueue = Promise.resolve();
mermaid.render = (id, source) => {
  const call = { source, size: null };
  renders.push(call);
  const run = mermaidQueue.then(async () => {
    call.size = inits.at(-1)?.themeVariables?.fontSize;
    if (hold) await new Promise((release) => { call.release = release; hold.push(call); });
    // What a layout failure looks like from mermaid: a TypeError out of its own code.
    if (call.fail) throw new TypeError("Cannot read properties of null (reading 'getBBox')");
    return { svg: `<svg data-size="${call.size}"></svg>` };
  });
  mermaidQueue = run.catch(() => {});
  return run;
};

const settle = async () => { for (let i = 0; i < 8; i++) await new Promise((r) => setTimeout(r, 0)); };
const frames = () => [...ed.window.document.querySelectorAll('.mdm-mermaid')];
const sizes = () => frames().map((f) => f.querySelector('svg')?.getAttribute('data-size') ?? f.textContent);
const view = () => ed.view();
/** Let everything still waiting run to the end. */
async function drain() {
  const waiting = hold ?? [];
  hold = null;
  for (let i = 0; i < 10; i++) { waiting.splice(0).forEach((c) => c.release()); await settle(); }
}

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
  // Two copies on screen now, and both are asked for before either has finished, so
  // neither can be served the other's: one render each, as before this change.
  const before = renders.length;
  assert.equal(setMermaidTheme(view(), 'dark', 32), true);
  await settle();
  assert.equal(inits.at(-1).theme, 'dark');
  assert.equal(inits.at(-1).themeVariables.fontSize, '32px', 'a theme switch dropped the size');
  assert.equal(renders.length, before + 2);
  assert.deepEqual(sizes(), ['32px', '32px']);
});

test('a journey, a radar chart and an event model are drawn at mermaid\'s own size, among flowcharts at the document\'s', async () => {
  // One document, alternating, so a size set for one diagram has to be put back for the next.
  const fence = (s) => '```mermaid\n' + s + '\n```';
  ed.roundTrip([
    fence('graph LR\n    J[Journey next] --> K[Then radar]'),
    fence('journey\n    title Third watch\n    section Listening\n      Take over the watch: 5: Sparks'),
    fence('graph TD\n    R[Radar next]'),
    fence('radar-beta\n  axis m["Morse"], v["Voice"], l["Logging"]\n  curve a["Sparks"]{4, 3, 5}'),
    fence('eventmodeling\n  tf 01 evt WatchStarted'),
    fence('graph TD\n    E[After the event model]'),
  ].join('\n\n') + '\n');
  await settle();
  assert.deepEqual(sizes(), ['32px', '16px', '32px', '16px', '16px', '32px']);
});

test('a diagram asked for at one size is drawn at that size, even when the size has moved on before its turn', async () => {
  // The replay: 40 -> 24 -> 40 -> 24, with the first 24px diagram waiting behind a 40px one.
  // Mermaid's config is global and read when a render starts, so if the size were handed to
  // mermaid at the switch, that 24px diagram would start under the later 40 and be drawn at
  // 40 - and cached as the 24px drawing, which a copy added in time would show for good.
  ed.roundTrip(`Some prose.\n\n${DIAGRAM}\n`);
  await settle();
  const start = renders.length;
  hold = [];
  setMermaidTheme(view(), 'dark', 40); await settle();   // starts, and waits
  setMermaidTheme(view(), 'dark', 24); await settle();   // queued behind it
  setMermaidTheme(view(), 'dark', 40); await settle();   // queued
  hold.shift().release(); await settle();                  // the first finishes; the 24 starts
  setMermaidTheme(view(), 'dark', 24); await settle();   // the look is 24 again
  hold.shift().release(); await settle();                  // the waiting 24 finishes, superseded
  ed.roundTrip(`Some prose.\n\n${DIAGRAM}\n\n${DIAGRAM}\n`);  // a copy, before the new 24 lands
  await settle();
  await drain();

  const drawn = renders.slice(start).map((r) => r.size);
  assert.deepEqual(drawn.slice(0, 4), ['40px', '24px', '40px', '24px'], 'a diagram was drawn under a later look than its own');
  assert.deepEqual(drawn.slice(4).filter((s) => s !== '24px'), []);
  assert.deepEqual(sizes(), ['24px', '24px']);
});

test('a render that fails after its look was superseded is not kept as that look\'s drawing', async () => {
  // The same replay, with the superseded 24px render failing. Its failure is not what the
  // diagram draws as: kept, it would be the error every copy of the diagram shows at 24px
  // for as long as the look lasts, though the diagram itself is fine.
  ed.roundTrip(`Some prose.\n\n${DIAGRAM}\n`);
  await settle();
  hold = [];
  setMermaidTheme(view(), 'dark', 40); await settle();
  setMermaidTheme(view(), 'dark', 24); await settle();
  setMermaidTheme(view(), 'dark', 40); await settle();
  hold.shift().release(); await settle();
  setMermaidTheme(view(), 'dark', 24); await settle();
  const failing = hold.shift();
  assert.equal(failing.size, '24px');
  failing.fail = true;
  failing.release(); await settle();
  ed.roundTrip(`Some prose.\n\n${DIAGRAM}\n\n${DIAGRAM}\n`);
  await settle();
  await drain();

  assert.deepEqual(sizes(), ['24px', '24px'], 'a superseded failure is showing as the diagram');
  assert.equal(frames().some((f) => f.classList.contains('mdm-mermaid-error')), false);
});
