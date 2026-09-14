// Milkdown's parser lists its types once per parser (patches/@milkdown+transformer+*.patch), not once per markdown node: faster, and nothing else.
import test, { before } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { parserCtx, remarkCtx, schemaCtx } from '@milkdown/kit/core';
import { ParserState } from '@milkdown/kit/transformer';
import { mountEditor } from './jsdom-editor.mjs';

let ctx;
before(async () => { ctx = (await mountEditor()).editor.ctx; });
const read = (file) => readFileSync(new URL(file, import.meta.url), 'utf8');

// The parse as it was: each node matched against the types listed afresh for it. Everything else is the parser's own.
function unpatchedParse(md) {
  const schema = ctx.get(schemaCtx), state = new ParserState(schema);
  state.next = (nodes = []) => {
    for (const node of [nodes].flat()) {
      const type = Object.values({ ...schema.nodes, ...schema.marks }).find((x) => x.spec.parseMarkdown.match(node));
      type.spec.parseMarkdown.runner(state, node, type);
    }
    return state;
  };
  return state.run(ctx.get(remarkCtx), md).toDoc();
}
for (const file of ['../../CHANGELOG.md', '../../HELP.md', 'fixtures/roundtrip-audit.md']) {
  test(`${file} parses to the document it parsed to before the patch`, () => {
    assert.deepEqual(ctx.get(parserCtx)(read(file)).toJSON(), unpatchedParse(read(file)).toJSON());
  });
}
test('a parse lists the types once, however many nodes it matches', () => {
  let listed = 0, schema = ctx.get(schemaCtx);
  const nodes = new Proxy(schema.nodes, { ownKeys: (t) => { listed++; return Reflect.ownKeys(t); } });
  new ParserState(Object.create(schema, { nodes: { value: nodes } })).run(ctx.get(remarkCtx), read('fixtures/roundtrip-audit.md')).toDoc();
  assert.equal(listed, 1);
});
