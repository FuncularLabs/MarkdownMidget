// The first diagram of a session, on its own: a file of its own is a fresh process, so
// mermaid has never been initialised when this diagram asks to be drawn. Mermaid only
// knows its diagram types once it has been, so until then it can't tell a journey from a
// flowchart, and a journey drawn first would get the document's size — the breakage the
// kept-size types exist to prevent (test/mermaid-render.test.mjs starts with a flowchart
// at 16px, where the mistake can't show).
import test from 'node:test';
import assert from 'node:assert/strict';
import { mountEditor } from './jsdom-editor.mjs';

const ed = await mountEditor('');
const { default: mermaid } = await import('mermaid');
const { setMermaidTheme } = await import('../src/mermaid.js');

const inits = [];
const realInitialize = mermaid.initialize.bind(mermaid);
mermaid.initialize = (config) => { inits.push(config); realInitialize(config); };
mermaid.render = async () => ({ svg: `<svg data-size="${inits.at(-1)?.themeVariables?.fontSize}"></svg>` });

test('a journey opened first at double size is drawn at mermaid\'s own size', async () => {
  setMermaidTheme(ed.view(), 'dark', 32);
  assert.equal(inits.length, 0, 'mermaid was initialised before any diagram asked to be drawn');
  ed.roundTrip('```mermaid\njourney\n    title Third watch\n    section Listening\n      Take over the watch: 5: Sparks\n```\n');
  for (let i = 0; i < 8; i++) await new Promise((r) => setTimeout(r, 0));
  const frame = ed.window.document.querySelector('.mdm-mermaid svg');
  assert.equal(frame?.getAttribute('data-size'), '16px');
});
