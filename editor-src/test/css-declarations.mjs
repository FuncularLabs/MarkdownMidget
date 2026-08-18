// Flattens a stylesheet to an ordered list of "where, what, value" records, with
// every `var(--x)` resolved back to the literal it stands for.
//
// This exists for one job: proving that pulling ~40 colours out of editor.css into
// a theme file changed nothing. Screenshots can't do that — two headings that
// collapse from #606060 and #707070 into one value look identical at a glance and
// are a permanent, invisible regression. Comparing resolved declarations can.
import postcss from 'postcss';

/** `@media print` → `.a, .b` → `color` → `#000`, in document order. */
export function declarations(css, variables = new Map()) {
  const root = postcss.parse(css);
  const out = [];

  const walk = (node, context) => {
    node.each?.((child) => {
      if (child.type === 'atrule') {
        // `@page { margin }` and `@media print { … }` both carry declarations that
        // belong to their context, not to the enclosing one.
        const at = `@${child.name}${child.params ? ' ' + child.params : ''}`;
        walk(child, context ? `${context} > ${at}` : at);
      } else if (child.type === 'rule') {
        walk(child, context ? `${context} > ${selector(child)}` : selector(child));
      } else if (child.type === 'decl') {
        out.push({
          where: context,
          prop: child.prop.trim().toLowerCase(),
          value: resolve(normalize(child.value), variables),
          important: child.important === true,
        });
      }
    });
  };

  walk(root, '');
  return out;
}

/** The custom properties a stylesheet defines on `:root`. */
export function rootVariables(css) {
  const vars = new Map();
  postcss.parse(css).walkRules((rule) => {
    if (selector(rule) !== ':root') return;
    rule.walkDecls((decl) => {
      if (decl.prop.startsWith('--')) vars.set(decl.prop.trim(), normalize(decl.value));
    });
  });
  return vars;
}

// Selector lists are order- and whitespace-insensitive to a browser; a refactor
// that reflows one across lines must not read as a change.
const selector = (rule) =>
  rule.selectors.map((s) => s.replace(/\s+/g, ' ').trim()).sort().join(', ');

function normalize(value) {
  return value
    .replace(/\/\*[\s\S]*?\*\//g, ' ')
    .replace(/\s+/g, ' ')
    .replace(/\s*,\s*/g, ', ')
    // #ABC → #aabbcc, so a shorthand rewrite doesn't read as a colour change.
    .replace(/#([0-9a-fA-F]{3})\b/g, (_, h) => '#' + [...h].map((c) => c + c).join(''))
    .replace(/#[0-9a-fA-F]{6}\b/g, (h) => h.toLowerCase())
    .trim();
}

/**
 * Substitute `var(--x, fallback)` recursively.
 *
 * An unknown variable keeps its `var()` text rather than silently becoming its
 * fallback or an empty string: a typo'd name that quietly resolved to nothing is
 * exactly the failure this is meant to catch, and it must show up as a difference.
 */
function resolve(value, variables, depth = 0) {
  if (depth > 10 || !value.includes('var(')) return value;

  const start = value.indexOf('var(');
  let level = 0;
  let end = -1;
  for (let i = start + 3; i < value.length; i++) {
    if (value[i] === '(') level++;
    else if (value[i] === ')' && --level === 0) { end = i; break; }
  }
  if (end < 0) return value;

  const inner = value.slice(start + 4, end);
  const comma = splitTopLevel(inner);
  const name = comma[0].trim();

  // The doc comment above is the contract and the code now matches it: an
  // UNKNOWN variable keeps its var() text even when a fallback exists. Taking
  // the fallback here silently erased the variable's NAME from the value —
  // which broke the twins pins the moment print.css grew the first
  // fallback-form var() in these files, and would equally have hidden a typo'd
  // name behind its fallback, the exact failure this resolver exists to show.
  let replacement;
  if (variables.has(name)) replacement = variables.get(name);
  else return value.slice(0, end + 1) + resolve(value.slice(end + 1), variables, depth + 1);

  return resolve(value.slice(0, start) + replacement + value.slice(end + 1), variables, depth + 1);
}

function splitTopLevel(text) {
  const parts = [];
  let level = 0;
  let current = '';
  for (const ch of text) {
    if (ch === '(') level++;
    else if (ch === ')') level--;
    if (ch === ',' && level === 0) { parts.push(current); current = ''; continue; }
    current += ch;
  }
  parts.push(current);
  return parts;
}
