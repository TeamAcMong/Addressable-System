/*
 * Build a self-contained, clickable copy of the design canvas that opens from the file system.
 *
 * WHY THIS EXISTS
 * The design was authored as Design Component artboards (.dc.html) and published as a hosted
 * canvas. The artboards are in this repository; the thing that RUNS them was not, so clicking
 * through the design meant opening a link on someone else's server. A design nobody on the team can
 * open without a URL is a design that quietly stops existing.
 *
 * The hosted canvas embeds ~2.4 MB of editor code, which is not worth a place in git history. So
 * this generates the alternative: one HTML file with the artboards inlined and a small runtime that
 * renders them. Read-only - pan, click, read - not the editor.
 *
 * ONE SOURCE. The .dc.html files remain the only place the design is written. This script derives
 * from them and is committed alongside its output so the derivation is reproducible rather than a
 * one-off export somebody made once and then edited by hand.
 *
 *     node Documentation/design/build-local-preview.mjs
 *
 * Writes Documentation/DESIGN_PREVIEW.html.
 */

import fs from 'node:fs'
import path from 'node:path'
import { fileURLToPath } from 'node:url'

const HERE = path.dirname(fileURLToPath(import.meta.url))
const OUT = path.join(HERE, '..', 'DESIGN_PREVIEW.html')

/** The artboards, in reading order. Kept in step with canvas.json by the check below. */
const ORDER = [
  'Main.dc.html',
  'Flows.dc.html',
  'Decisions.dc.html',
  'States.dc.html',
  'Components.dc.html',
  'MenuMap.dc.html',
]

// ---------------------------------------------------------------- read

const canvas = JSON.parse(fs.readFileSync(path.join(HERE, 'canvas.json'), 'utf8'))

// An artboard added to the canvas and forgotten here would be silently missing from the preview,
// which is the "the list you did not update is the one nobody checks" failure this repository has
// already paid for once. Fail loudly instead.
const inCanvas = canvas.artboards.map((a) => a.file).sort()
const inOrder = [...ORDER].sort()
if (inCanvas.join('|') !== inOrder.join('|')) {
  console.error('canvas.json and ORDER disagree about which artboards exist:')
  console.error('  canvas.json:', inCanvas.join(', '))
  console.error('  this script:', inOrder.join(', '))
  process.exit(1)
}

const titleOf = Object.fromEntries(
  canvas.artboards.map((a) => [a.file, a.title || a.file.replace(/\.dc\.html$/, '')])
)
const sizeOf = Object.fromEntries(canvas.artboards.map((a) => [a.file, { w: a.w, h: a.h }]))

/*
 * The left angle bracket is escaped wherever board source is embedded in the page's own <script>. No artboard
 * contains a literal </script> today, which is luck rather than a guarantee: one added later would
 * terminate the script tag early and the page would render as a wall of source with no error
 * anywhere. JSON.stringify does not do this for you.
 */

/** Pull the three parts a .dc.html carries: helmet styles, template markup, logic class. */
function parse(file) {
  const source = fs.readFileSync(path.join(HERE, file), 'utf8')

  const dc = source.match(/<x-dc>([\s\S]*?)<\/x-dc>/)
  if (!dc) throw new Error(`${file}: no <x-dc> block`)

  let body = dc[1]
  let style = ''

  const helmet = body.match(/<helmet>([\s\S]*?)<\/helmet>/)
  if (helmet) {
    const css = helmet[1].match(/<style>([\s\S]*?)<\/style>/)
    if (css) style = css[1]
    body = body.replace(helmet[0], '')
  }

  const script = source.match(/<script data-dc-script[^>]*>([\s\S]*?)<\/script>/)

  return { file, title: titleOf[file], size: sizeOf[file], style, template: body.trim(), logic: script ? script[1] : '' }
}

const boards = ORDER.map(parse)

// ---------------------------------------------------------------- the runtime that ships with it

const RUNTIME = String.raw`
// A small reader for the subset of the Design Component format these artboards use:
// {{dotted.paths}} in text and attributes, <sc-for>, <sc-if>, and onClick bound to a function the
// logic class returned. Not the editor - there is no editing here, and no attempt at one.
class DCLogic {
  constructor(props) { this.props = props || {}; this.state = {}; }
  setState(patch) { Object.assign(this.state, patch); this._rerender && this._rerender(); }
  renderVals() { return {}; }
}

function lookup(scope, path) {
  if (path === 'true') return true;
  if (path === 'false') return false;
  return path.split('.').reduce((v, k) => (v == null ? undefined : v[k]), scope);
}

function interpolate(text, scope) {
  return text.replace(/\{\{\s*([^}]+?)\s*\}\}/g, (_, p) => {
    const v = lookup(scope, p);
    return v == null ? '' : String(v);
  });
}

function isWholeHole(value) {
  return /^\{\{\s*[^}]+\s*\}\}$/.test(value.trim());
}

function renderNodes(nodes, scope, out) {
  for (const node of nodes) {
    if (node.nodeType === 3) {
      const text = interpolate(node.nodeValue, scope);
      if (text.trim() !== '' || /\s/.test(node.nodeValue)) out.appendChild(document.createTextNode(text));
      continue;
    }
    if (node.nodeType !== 1) continue;

    const tag = node.tagName.toLowerCase();

    if (tag === 'sc-for') {
      const list = lookup(scope, node.getAttribute('list').replace(/[{}]/g, '').trim()) || [];
      const as = node.getAttribute('as') || 'item';
      for (const item of list) {
        renderNodes([...node.childNodes], { ...scope, [as]: item }, out);
      }
      continue;
    }

    if (tag === 'sc-if') {
      const cond = lookup(scope, node.getAttribute('value').replace(/[{}]/g, '').trim());
      if (cond) renderNodes([...node.childNodes], scope, out);
      continue;
    }

    const el = document.createElementNS(node.namespaceURI, node.tagName);
    for (const attr of node.attributes) {
      if (attr.name === 'onClick' || attr.name === 'onclick') {
        if (isWholeHole(attr.value)) {
          const fn = lookup(scope, attr.value.replace(/[{}]/g, '').trim());
          if (typeof fn === 'function') el.addEventListener('click', fn);
        }
        continue;
      }
      el.setAttribute(attr.name, interpolate(attr.value, scope));
    }

    renderNodes([...node.childNodes], scope, el);
    out.appendChild(el);
  }
}

function mount(board, host) {
  const template = new DOMParser().parseFromString('<div>' + board.template + '</div>', 'text/html')
    .body.firstChild;

  const Component = board.logic
    ? new Function('DCLogic', board.logic + '; return Component;')(DCLogic)
    : class extends DCLogic {};

  const instance = new Component({});
  const draw = () => {
    host.textContent = '';
    renderNodes([...template.childNodes], instance.renderVals(), host);
  };
  instance._rerender = draw;
  draw();
}
`

// ---------------------------------------------------------------- emit

const html = `<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>Design preview — Addressable Manager UI</title>
<style>
  :root { color-scheme: light dark; }
  body {
    margin: 0; background: #1a1a1e; color: #d2d2d2;
    font: 14px/1.6 -apple-system, BlinkMacSystemFont, "Segoe UI", Roboto, sans-serif;
  }
  header {
    padding: 22px 26px 18px; border-bottom: 1px solid #2b2b31;
    position: sticky; top: 0; background: #1a1a1e; z-index: 10;
  }
  h1 { margin: 0 0 6px; font-size: 17px; letter-spacing: -0.01em; }
  .sub { margin: 0; font-size: 12px; color: #8a8a8a; max-width: 760px; }
  nav { margin-top: 12px; display: flex; flex-wrap: wrap; gap: 6px; }
  nav a {
    font-size: 12px; padding: 3px 9px; border: 1px solid #34343c; border-radius: 3px;
    color: #b4b4b4; text-decoration: none;
  }
  nav a:hover { background: #26262c; color: #e4e4e4; }
  section { padding: 26px; border-bottom: 1px solid #2b2b31; }
  h2 { margin: 0 0 4px; font-size: 14px; font-weight: 600; }
  .meta { margin: 0 0 14px; font-size: 11px; color: #7e7e7e; }
  .frame {
    border: 1px solid #34343c; border-radius: 4px; overflow: auto;
    max-width: 100%; background: #383838;
  }
  footer { padding: 22px 26px 60px; font-size: 12px; color: #7e7e7e; }
  code { font-family: ui-monospace, SFMono-Regular, Consolas, monospace; font-size: 0.92em; }
</style>
</head>
<body>

<header>
  <h1>Design preview — Addressable Manager UI</h1>
  <p class="sub">
    Generated from <code>Documentation/design/*.dc.html</code> by
    <code>build-local-preview.mjs</code>. Opens from the file system, needs no network and no
    install. Read-only: the artboards are clickable where the design is, but this is not the editor.
    Change the design in the <code>.dc.html</code> sources and re-run the script.
  </p>
  <nav>
${boards.map((b, i) => `    <a href="#board-${i}">${b.title}</a>`).join('\n')}
  </nav>
</header>

${boards
  .map(
    (b, i) => `<section id="board-${i}">
  <h2>${b.title}</h2>
  <p class="meta">${b.file} · ${b.size.w}×${b.size.h}</p>
  <style>${b.style}</style>
  <div class="frame"><div id="host-${i}"></div></div>
</section>`
  )
  .join('\n\n')}

<footer>
  The interactive canvas this was generated from also exists as a hosted artifact. This file is the
  copy that survives without it.
</footer>

<script>
${RUNTIME}

const BOARDS = ${JSON.stringify(
  boards.map((b) => ({ template: b.template, logic: b.logic })),
  null,
  0
).replace(/</g, '\u003c')};

BOARDS.forEach((board, i) => {
  const host = document.getElementById('host-' + i);
  try {
    mount(board, host);
  } catch (err) {
    // A board that fails to render says so in place rather than leaving a blank frame, which would
    // be indistinguishable from a board that is meant to be empty.
    host.innerHTML = '<div style="padding:20px;color:#f2857c;font-size:13px">' +
      'This artboard did not render: ' + String(err) + '</div>';
    console.error(err);
  }
});
</script>

</body>
</html>
`

fs.writeFileSync(OUT, html, 'utf8')

const kb = (fs.statSync(OUT).size / 1024).toFixed(0)
console.log(`wrote ${path.relative(process.cwd(), OUT)} — ${boards.length} artboards, ${kb} KB`)
