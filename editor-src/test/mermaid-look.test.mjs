// What a diagram is drawn with: mermaid's own theme, and the document's text size.
//
// A diagram's labels are measured by mermaid and drawn into boxes sized to fit, so the
// size has to reach mermaid itself; a stylesheet can only make the text disagree with
// its box. These are the small decisions around that, each one testable without a
// layout engine: reading the size, what mermaid is handed, what the cache is keyed on,
// and when a change means a redraw. That mermaid then DRAWS at that size is a question
// only an engine answers — TEST-PLAN THM-04 measures it in WebView2.
import test from 'node:test';
import assert from 'node:assert/strict';
import mermaid from 'mermaid';
import {
  MERMAID_THEMES, DEFAULT_FONT_PX, KEPT_SIZE_TYPES, themeName, fontPx, documentFontPx,
  mermaidLook, sameLook, mermaidConfig, cacheKey,
} from '../src/mermaid-look.js';

test('a theme names one of mermaid\'s own built-ins, or gets default', () => {
  // The name comes out of a user's stylesheet, and mermaid throws on one it doesn't
  // know — which would put an error box where every diagram in the document was.
  for (const name of MERMAID_THEMES) assert.equal(themeName(name), name);
  for (const name of ['', 'Dark', 'midnight', undefined, null, 'default ']) assert.equal(themeName(name), 'default', String(name));
});

test('the size is a px length, and anything that is not a positive one is 16', () => {
  assert.equal(DEFAULT_FONT_PX, 16);
  assert.equal(fontPx('32px'), 32);
  assert.equal(fontPx(' 21.5px '), 21.5);
  assert.equal(fontPx(24), 24);
  // What a registered <length> can't come back as, and what an unregistered one could.
  for (const bad of ['', 'banana', '2em', '0px', '-4px', 'px', '32', NaN, 0, -1, Infinity, undefined, null]) {
    assert.equal(fontPx(bad), 16, `${String(bad)} should read as 16`);
  }
});

/** Just enough of a document and a window for documentFontPx, which reads one property. */
function page({ editor, root }) {
  const els = { editor: { name: 'editor', size: editor }, root: { name: 'root', size: root } };
  const doc = {
    documentElement: els.root,
    querySelector: (sel) => (sel === '.mdm-prosemirror' && editor !== undefined ? els.editor : null),
  };
  const asked = [];
  const win = {
    getComputedStyle(el) {
      return { getPropertyValue(prop) { asked.push([el.name, prop]); return el.size; } };
    },
  };
  return { doc, win, asked };
}

test('the document\'s size is --mdm-font-size as the editor sees it, or as the page does before there is one', () => {
  // The editor root when there is one, so a size a theme declares there is the size read.
  const a = page({ editor: ' 32px', root: '16px' });
  assert.equal(documentFontPx(a.doc, a.win), 32);
  assert.deepEqual(a.asked, [['editor', '--mdm-font-size']]);

  const b = page({ editor: undefined, root: '24px' });
  assert.equal(documentFontPx(b.doc, b.win), 24);
  assert.deepEqual(b.asked, [['root', '--mdm-font-size']]);

  // Nothing readable is the default size, not a thrown error and not 0.
  const c = page({ editor: '', root: '' });
  assert.equal(documentFontPx(c.doc, c.win), 16);
});

test('mermaid is handed the size as themeVariables.fontSize, beside the settings it always had', () => {
  assert.deepEqual(mermaidConfig(mermaidLook('dark', 32)), {
    startOnLoad: false,
    theme: 'dark',
    securityLevel: 'strict',
    themeVariables: { fontSize: '32px' },
  });
  assert.deepEqual(mermaidConfig(mermaidLook('nonsense', 'banana')).themeVariables, { fontSize: '16px' });
});

test('mermaid 11 keeps that size in every theme we name, at 16px hands it exactly its own default, and leaves the pie\'s sizes alone', () => {
  // Not assumed: mermaid documents themeVariables as a `base`-theme feature. What it
  // does in code is recompute every theme from the overrides it was initialised with,
  // so each of the five is checked. Top-level `fontSize` is NOT the lever — mermaid
  // reads it only to size an image-only label; text follows themeVariables.fontSize.
  for (const theme of MERMAID_THEMES) {
    mermaid.initialize({ startOnLoad: false, theme, securityLevel: 'strict' });
    const own = mermaid.mermaidAPI.getConfig().themeVariables;

    mermaid.initialize(mermaidConfig(mermaidLook(theme, 16)));
    assert.equal(mermaid.mermaidAPI.getConfig().themeVariables.fontSize, own.fontSize,
      `${theme}: at 16px mermaid is not handed its own default`);

    mermaid.initialize(mermaidConfig(mermaidLook(theme, 32)));
    const at32 = mermaid.mermaidAPI.getConfig().themeVariables;
    assert.equal(at32.fontSize, '32px', `${theme} dropped the size`);

    // The pie sizes its three texts from variables of its own, against geometry that
    // does not grow: a 450-unit pie, legend rows 22 units apart, a title 25 units from
    // the top. Doubled, they were measured overlapping the legend rows by 14.6px and
    // pushing the title 18px out of the drawing, so they are deliberately not scaled.
    for (const v of ['pieTitleTextSize', 'pieSectionTextSize', 'pieLegendTextSize']) {
      assert.equal(at32[v], own[v], `${theme}: ${v} moved with the document size`);
    }
  }
});

test('a new size or a new theme is a new look, and the same pair is not', () => {
  const base = mermaidLook('dark', '32px');
  assert.equal(sameLook(base, mermaidLook('dark', 32)), true);
  assert.equal(sameLook(base, mermaidLook('dark', '16px')), false);
  assert.equal(sameLook(base, mermaidLook('forest', '32px')), false);
  // A name mermaid doesn't know is 'default', so it is no change from default.
  assert.equal(sameLook(mermaidLook('default', 16), mermaidLook('midnight', 'banana')), true);
});

test('seventeen diagram types are drawn at mermaid\'s own size, every other type at the document\'s', () => {
  // Measured in Edge at 32px, four types grow their text inside geometry that does not grow
  // with it: a journey's task labels spilled out of their fixed 150-unit boxes and its title
  // was cut at the top; a radar chart's title was cut and ran into an axis label; an event
  // model's event boxes kept their width around text twice the size; a requirement's rows
  // ran into one another and across its divider, where at 16px they are clean. A stylesheet
  // can't take it back afterwards, which was tried: a journey measures its legend at the size
  // it is given, and a radar chart sizes its title in a rule of its own. The other thirteen
  // size their text in settings of their own and never grew; listing them means nothing in
  // them can, whichever of their elements a document uses. THM-04 has the survey.
  const drawnSmall = ['journey', 'radar', 'eventmodeling', 'requirement'];
  const neverGrew = ['sequence', 'gantt', 'pie', 'quadrantChart', 'xychart', 'sankey', 'packet',
    'treemap', 'venn', 'wardley', 'cynefin', 'treeView', 'info'];
  assert.deepEqual([...KEPT_SIZE_TYPES].sort(), [...drawnSmall, ...neverGrew].sort());
  const big = mermaidLook('dark', 32);
  for (const type of KEPT_SIZE_TYPES) {
    assert.equal(mermaidConfig(big, type).themeVariables.fontSize, `${DEFAULT_FONT_PX}px`, type);
  }
  // The types that grow and fit, by mermaid's ids, and a source mermaid can't place.
  for (const type of ['flowchart-v2', 'flowchart', 'class', 'classDiagram', 'state', 'stateDiagram', 'er',
    'mindmap', 'timeline', 'kanban', 'block', 'architecture', 'gitGraph', 'c4',
    'ishikawa', 'railroad', 'railroadEbnf', 'swimlane', null, undefined]) {
    assert.equal(mermaidConfig(big, type).themeVariables.fontSize, '32px', String(type));
  }

  // The names are mermaid's own: what it detects each kind of source as, which is also what it
  // writes as the SVG's aria-roledescription. A directive in front changes nothing.
  mermaid.initialize(mermaidConfig(big));
  const sources = {
    journey: 'journey\n  title Watch\n  section Listening\n    Take over: 5: Sparks',
    radar: 'radar-beta\n  axis m["Morse"], v["Voice"], l["Log"]\n  curve a["Sparks"]{4, 3, 5}',
    eventmodeling: 'eventmodeling\n  tf 01 evt WatchStarted',
    requirement: 'requirementDiagram\n  element log {\n    type: book\n  }',
    sequence: 'sequenceDiagram\n  A->>B: Hello',
    gantt: 'gantt\n  section Watch\n  Listening :a1, 2026-01-01, 1d',
    pie: 'pie title Watch\n  "Traffic" : 26',
    quadrantChart: 'quadrantChart\n  Distress: [0.9, 0.2]',
    xychart: 'xychart-beta\n  bar [12, 30]',
    sankey: 'sankey-beta\nTraffic,Log,20',
    packet: 'packet-beta\n0-15: "Source Port"',
    treemap: 'treemap-beta\n"Watch"\n    "Listening": 60',
    venn: 'venn-beta\n  set A["Morse"]',
    wardley: 'wardley-beta\n  title Wireless room',
    cynefin: 'cynefin-beta\n  title Watch decisions',
    treeView: 'treeView-beta\n  wireless-room',
    info: 'info',
  };
  assert.deepEqual(Object.keys(sources).sort(), [...KEPT_SIZE_TYPES].sort());
  for (const [type, source] of Object.entries(sources)) {
    assert.equal(mermaid.detectType(source), type);
    assert.equal(mermaid.detectType(`%%{init: {'theme': 'forest'}}%%\n${source}`), type);
  }
});

test('a drawing is cached under the look it was asked for', () => {
  // mermaid.js draws each diagram under the look its widget asked for, so that is also the
  // look the drawing was made in (test/mermaid-render.test.mjs holds that half).
  const src = 'graph LR\n  A --> B';
  const at16 = mermaidLook('dark', 16);
  assert.equal(cacheKey(at16, src), cacheKey(mermaidLook('dark', '16px'), src));
  assert.notEqual(cacheKey(at16, src), cacheKey(mermaidLook('dark', 32), src), 'a 16px drawing would be served at 32px');
  assert.notEqual(cacheKey(at16, src), cacheKey(mermaidLook('forest', 16), src), 'a dark drawing would be served under forest');
  assert.notEqual(cacheKey(at16, src), cacheKey(at16, src + ' '));
  // The parts can't run into each other: 1px of "6x" is not 16px of "x".
  assert.notEqual(cacheKey(mermaidLook('dark', 1), '6x'), cacheKey(mermaidLook('dark', 16), 'x'));
});
