// YAML front matter, a `---` … `---` block at the very start of a file (Jekyll, Hugo, Obsidian): parsed by remark-frontmatter, held as a code
// block whose text is its source, fences included, so a save writes back the bytes it was read from, and the blank lines after it (`gap`). Its
// serialiser, which drops what follows a fence, is replaced. Where a parse would not read the text back as front matter (not first, or a fence
// edited away) it is written as a ```yaml code block. It pastes as a code block (no parseDOM), and no command wraps or retypes it (keepFrontMatter).
import { $nodeSchema, $remark, $prose } from '@milkdown/kit/utils';
import { Plugin, PluginKey } from '@milkdown/kit/prose/state';
import { ReplaceAroundStep } from '@milkdown/kit/prose/transform';
import { defaultHandlers } from 'mdast-util-to-markdown';
import remarkFrontmatter from 'remark-frontmatter';

// What micromark-extension-frontmatter reads as front matter: `---` and spaces or tabs, a line ending, lines that are not such a fence, and one.
const FENCED = /^---[ \t]*(?:\r\n|\r(?!\n)|\n)(?:(?!---[ \t]*(?:[\r\n]|$))[^\r\n]*(?:\r\n|\r(?!\n)|\n))*---[ \t]*$/;
const BLANK = /(?:\r\n|\r|\n)((?:[ \t]*(?:\r\n|\r|\n))*)/y;   // the end of the closing fence's line, and the blank lines after it

const TAIL = /(?:(?:\r\n|\r(?!\n)|\n)[ \t]*)*$/;   // empty lines at the end of its text, as Enter after the closing fence leaves
const eols = (s) => (s.match(/\r\n|\r|\n/g) ?? []).length;

/** What a save writes as front matter: the text less an empty tail, whose lines join the gap after it; null where a parse would not read it back. */
const verbatim = (node, parent) => {
  const tail = TAIL.exec(node.value)[0], text = node.value.slice(0, node.value.length - tail.length);
  return parent?.type === 'root' && parent.children[0] === node && FENCED.test(text) ? { text, gap: node.gap + eols(tail) } : null;
};
/** The mdast `yaml` handler: that text, else a ```yaml code block of all of it, joined to what follows as any code block is. */
const write = (node, parent, state, info) => verbatim(node, parent)?.text ?? defaultHandlers.code({ type: 'code', lang: 'yaml', value: node.value }, parent, state, info);

const remarkFrontMatter = $remark('mdmFrontMatter', () => function frontMatter() {
  const at = this.data().toMarkdownExtensions?.length ?? 0;
  remarkFrontmatter.call(this, 'yaml');
  this.data().toMarkdownExtensions.splice(at, 1, { handlers: { yaml: write }, join: [(left, _, parent) => (left.type === 'yaml' ? verbatim(left, parent)?.gap : undefined)] });
  return (tree, file) => {
    const node = tree.children[0], text = String(file.value).replace(/^\u{FEFF}/u, ''), end = node?.position?.end.offset;   // the parse's offsets skip a byte-order mark
    if (node?.type !== 'yaml') return;
    BLANK.lastIndex = end;
    const after = BLANK.exec(text);
    node.value = text.slice(node.position.start.offset, end);
    node.gap = after ? eols(after[1]) : 0;   // none: the file ends with the closing fence
  };
});

const frontMatterSchema = $nodeSchema('front_matter', () => ({
  content: 'text*', group: 'block', marks: '', code: true, defining: true, attrs: { gap: { default: 1 } },
  toDOM: () => ['pre', { class: 'mdm-front-matter', 'data-language': 'yaml', title: 'Front matter' }, ['code', 0]],   // a code block's look, fences and all
  parseMarkdown: {
    match: (node) => node.type === 'yaml',
    runner: (state, node, type) => { state.openNode(type, { gap: node.gap ?? 1 }); if (node.value) state.addText(node.value); state.closeNode(); },
  },
  toMarkdown: {
    match: (node) => node.type.name === 'front_matter',
    runner: (state, node) => { state.addNode('yaml', undefined, node.textContent, { gap: node.attrs.gap }); },
  },
}));

/** A step that wraps, lifts or retypes blocks (a ReplaceAroundStep) is refused where it would take the front matter with it. */
const keepFrontMatter = $prose(() => new Plugin({
  key: new PluginKey('MDM_FRONT_MATTER'),
  filterTransaction: (tr) => !tr.steps.some((step, i) => {
    let hit = false;
    if (step instanceof ReplaceAroundStep) tr.docs[i].nodesBetween(step.from, step.to, (n) => { hit ||= n.type.name === 'front_matter'; });
    return hit;
  }),
}));

export const frontMatter = [remarkFrontMatter, frontMatterSchema, keepFrontMatter].flat();
