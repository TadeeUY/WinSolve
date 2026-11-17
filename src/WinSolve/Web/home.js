'use strict';
// The Home page. WinSolve (C#) sends the data and the texts already translated; this file only draws
// them. Every text is put in with textContent, never innerHTML: names of drives and programs come from
// the PC and must not be able to inject markup. Actions go back to WinSolve as small messages and
// WinSolve decides what is allowed (the page never names a task to run, only an item it was shown).

const bridge = window.chrome && window.chrome.webview;
const send = (message) => { if (bridge) bridge.postMessage(message); };
const $ = (id) => document.getElementById(id);

function el(tag, className, text) {
  const node = document.createElement(tag);
  if (className) node.className = className;
  if (text !== undefined && text !== null) node.textContent = text;
  return node;
}

function glyph(code, className) {
  return el('span', 'glyph ' + (className || ''), code);
}

function applyTheme(theme) {
  const root = document.documentElement;
  const map = {
    bg: '--bg', card: '--card', cardHover: '--card-hover', control: '--control', controlHover: '--control-hover',
    border: '--border', divider: '--divider', text: '--text', muted: '--muted', good: '--good', warn: '--warn',
    bad: '--bad', info: '--info', accent: '--accent', iconTile: '--icon-tile', track: '--track', inset: '--inset',
  };
  for (const [key, cssVar] of Object.entries(map)) if (theme[key]) root.style.setProperty(cssVar, theme[key]);
  if (theme.light) root.setAttribute('data-light', ''); else root.removeAttribute('data-light');
}

function drawRing(score, color, ofText) {
  const svgNs = 'http://www.w3.org/2000/svg';
  const size = 124, stroke = 10, r = (size - stroke) / 2, c = 2 * Math.PI * r;
  const svg = document.createElementNS(svgNs, 'svg');
  svg.setAttribute('width', size); svg.setAttribute('height', size); svg.setAttribute('viewBox', `0 0 ${size} ${size}`);
  const track = document.createElementNS(svgNs, 'circle');
  for (const [k, v] of Object.entries({ cx: size / 2, cy: size / 2, r, fill: 'none', 'stroke-width': stroke })) track.setAttribute(k, v);
  track.setAttribute('stroke', 'var(--track)');
  svg.appendChild(track);
  if (score !== null && score !== undefined) {
    const arc = document.createElementNS(svgNs, 'circle');
    for (const [k, v] of Object.entries({ cx: size / 2, cy: size / 2, r, fill: 'none', 'stroke-width': stroke, 'stroke-linecap': 'round' })) arc.setAttribute(k, v);
    arc.setAttribute('stroke', color || 'var(--good)');
    arc.setAttribute('stroke-dasharray', `${(c * score) / 100} ${c}`);
    arc.setAttribute('transform', `rotate(-90 ${size / 2} ${size / 2})`);
    svg.appendChild(arc);
    const num = document.createElementNS(svgNs, 'text');
    num.setAttribute('x', size / 2); num.setAttribute('y', size / 2 + 6); num.setAttribute('text-anchor', 'middle'); num.setAttribute('class', 'num');
    num.textContent = String(score);
    const of = document.createElementNS(svgNs, 'text');
    of.setAttribute('x', size / 2); of.setAttribute('y', size / 2 + 24); of.setAttribute('text-anchor', 'middle'); of.setAttribute('class', 'of');
    of.textContent = ofText || '';
    svg.append(num, of);
  }
  const ring = $('ring');
  ring.replaceChildren(svg);
  ring.setAttribute('aria-label', score === null || score === undefined ? '' : `${score} / 100`);
}

function init(m) {
  applyTheme(m.theme);
  const t = m.text;
  document.documentElement.lang = t.lang || 'en';
  document.title = t.title;
  $('title').textContent = t.title;
  $('subtitle').textContent = t.subtitle;
  $('searchText').textContent = t.search;
  $('optimizeText').textContent = t.optimize;
  $('scanText').textContent = t.scan;
  $('thisPc').textContent = t.thisPc;
  $('ring').dataset.of = t.of;

  const quick = $('quick');
  quick.replaceChildren();
  for (const q of t.quick) {
    const b = el('button', 'tile-action');
    b.type = 'button';
    b.append(el('div', 'icon-tile glyph', q.glyph));
    const text = el('div');
    text.append(el('div', 't', q.title), el('div', 's', q.subtitle));
    b.append(text, el('span', 'chev glyph', ''));
    b.addEventListener('click', () => send({ type: 'navigate', key: q.target }));
    quick.append(b);
  }
}

function setBusy(busy, scanLabel) {
  $('scan').disabled = busy;
  if (scanLabel) $('scanText').textContent = scanLabel;
}

function state(m) {
  $('summary').textContent = m.summary || '';
  $('status').textContent = m.status || '';
  if (m.busy) drawRing(null);
  setBusy(!!m.busy, m.scanLabel);
}

function tile(caption, value, detail, color) {
  const node = el('div', 'tile');
  node.append(el('div', 'muted small', caption));
  const v = el('div', 'v', value);
  if (color) v.style.color = color;
  v.title = value;
  node.append(v, el('div', 'muted small', detail));
  return node;
}

function result(m) {
  drawRing(m.score, m.ringColor, $('ring').dataset.of);
  $('summary').textContent = m.summary;
  $('status').textContent = m.status;
  setBusy(false, m.scanLabel);

  const tiles = $('tiles');
  tiles.replaceChildren(...m.tiles.map((x) => tile(x.caption, x.value, x.detail, x.color)));

  const box = $('issues');
  box.replaceChildren(el('h2', null, m.issuesTitle));
  m.issues.forEach((issue, index) => {
    box.append(el('hr'));
    const row = el('div', 'issue');
    const dot = el('span', 'dot');
    dot.style.background = issue.color;
    const body = el('div', 'body');
    body.append(el('div', 'tt', issue.title), el('div', 'dd', issue.detail));
    row.append(dot, body);
    if (issue.fixLabel) {
      const fix = el('button', 'btn', issue.fixLabel);
      fix.type = 'button';
      fix.addEventListener('click', () => send({ type: 'fix', index }));
      row.append(fix);
    }
    box.append(row);
  });
  if (m.issues.length === 0) box.append(el('p', 'muted', m.emptyText));
  box.append(el('hr'), el('div', 'tt', m.componentsTitle));
  for (const c of m.components) {
    const line = el('div', 'comp');
    const value = el('div', 'v');
    const dot = el('span', 'dot');
    dot.style.background = c.color;
    value.append(dot, el('span', null, c.value));
    line.append(el('div', 'k', c.name), value);
    box.append(line);
  }
}

function weekly(m) {
  const card = $('weekly');
  if (!m.data) { card.hidden = true; card.replaceChildren(); return; }
  const d = m.data;
  const head = el('div', 'weekly-head');
  head.append(el('div', 'icon-tile glyph', ''));
  const titles = el('div');
  titles.append(el('h2', null, d.title), el('div', 'muted small', d.range));
  head.append(titles);
  const stats = el('div', 'stats');
  for (const s of d.stats) {
    const cell = el('div');
    const v = el('div', 'v', s.value);
    if (s.color) v.style.color = s.color;
    cell.append(el('div', 'muted small', s.title), v, el('div', 'muted small', s.detail));
    stats.append(cell);
  }
  const row = el('div', 'row');
  const ok = el('button', 'btn primary', d.gotIt);
  ok.type = 'button';
  ok.addEventListener('click', () => send({ type: 'weekly', action: 'dismiss' }));
  const off = el('button', 'btn', d.hide);
  off.type = 'button';
  off.addEventListener('click', () => send({ type: 'weekly', action: 'off' }));
  row.append(ok, off);
  card.replaceChildren(head, stats, row);
  card.hidden = false;
}

const handlers = { init, state, result, weekly };
if (bridge) {
  bridge.addEventListener('message', (e) => {
    const m = e.data;
    if (m && typeof m.type === 'string' && Object.hasOwn(handlers, m.type)) handlers[m.type](m);
  });
}

$('scan').addEventListener('click', () => send({ type: 'scan' }));
$('optimize').addEventListener('click', () => send({ type: 'navigate', key: 'optimize' }));
$('search').addEventListener('click', () => send({ type: 'search' }));
document.addEventListener('keydown', (e) => {
  if ((e.ctrlKey || e.metaKey) && e.key.toLowerCase() === 'k') { e.preventDefault(); send({ type: 'search' }); }
});
document.addEventListener('contextmenu', (e) => e.preventDefault());
drawRing(null);
send({ type: 'ready' });
