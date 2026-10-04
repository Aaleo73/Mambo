'use strict';
/*
 * Mambo 交互原型（P2 设计稿）。原生 JS，无外部依赖。
 * 所有图片都由下面的程序化生成器绘制，不使用任何外部或旧项目素材。
 */

/* ================= 工具 ================= */
const $ = (s, root = document) => root.querySelector(s);
const esc = s => String(s ?? '').replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
function hashStr(s) { let h = 2166136261; for (let i = 0; i < s.length; i++) { h ^= s.charCodeAt(i); h = Math.imul(h, 16777619); } return h >>> 0; }
function rng(seed) {
  let a = hashStr(String(seed));
  return () => { a |= 0; a = a + 0x6D2B79F5 | 0; let t = Math.imul(a ^ a >>> 15, 1 | a); t = t + Math.imul(t ^ t >>> 7, 61 | t) ^ t; return ((t ^ t >>> 14) >>> 0) / 4294967296; };
}
const normH = h => ((Math.round(h) % 360) + 360) % 360;
const hsl = (h, s, l) => `hsl(${normH(h)},${s}%,${l}%)`;
const f1 = n => Math.round(n * 10) / 10;
const pad2 = n => String(n).padStart(2, '0');
function fmtTime(t) {
  t = Math.max(0, Math.floor(t));
  const h = Math.floor(t / 3600), m = Math.floor(t % 3600 / 60), s = t % 60;
  return h ? `${h}:${pad2(m)}:${pad2(s)}` : `${m}:${pad2(s)}`;
}
function fmtMinutes(min) { const h = Math.floor(min / 60), m = min % 60; return h ? (m ? `${h} 小时 ${m} 分钟` : `${h} 小时`) : `${m} 分钟`; }

/* ================= 图标（24×24，线宽 1.6） ================= */
const ICON = {
  back: '<path d="M15 5 8 12l7 7"/>',
  forward: '<path d="m9 5 7 7-7 7"/>',
  search: '<circle cx="11" cy="11" r="6.5"/><path d="m16 16 4 4"/>',
  home: '<path d="M4 10.5 12 4l8 6.5V19a1 1 0 0 1-1 1h-4.5v-5.5h-5V20H5a1 1 0 0 1-1-1z"/>',
  recent: '<circle cx="12" cy="12" r="8"/><path d="M12 7.5V12l3 2"/>',
  movie: '<rect x="3.5" y="5" width="17" height="14" rx="2"/><path d="M3.5 9h17M3.5 15h17M8 5v4M16 5v4M8 15v4M16 15v4"/>',
  tv: '<rect x="3" y="5" width="18" height="12" rx="2"/><path d="M8.5 20.5h7M12 17v3.5"/>',
  folder: '<path d="M3.5 7.5a2 2 0 0 1 2-2h4l2 2h7a2 2 0 0 1 2 2V17a2 2 0 0 1-2 2h-13a2 2 0 0 1-2-2z"/>',
  play: '<path d="M8 5.6v12.8a.8.8 0 0 0 1.2.7l10.2-6.4a.8.8 0 0 0 0-1.4L9.2 4.9A.8.8 0 0 0 8 5.6z" fill="currentColor" stroke="none"/>',
  pause: '<rect x="6.5" y="5" width="4" height="14" rx="1.2" fill="currentColor" stroke="none"/><rect x="13.5" y="5" width="4" height="14" rx="1.2" fill="currentColor" stroke="none"/>',
  prev: '<path d="M6.5 6v12"/><path d="M18 6.6v10.8a.6.6 0 0 1-.9.5l-8.4-5.4a.6.6 0 0 1 0-1l8.4-5.4a.6.6 0 0 1 .9.5z" fill="currentColor" stroke="none"/>',
  next: '<path d="M17.5 6v12"/><path d="M6 6.6v10.8a.6.6 0 0 0 .9.5l8.4-5.4a.6.6 0 0 0 0-1L6.9 6.1a.6.6 0 0 0-.9.5z" fill="currentColor" stroke="none"/>',
  volume: '<path d="M4.5 9.5h3l4.5-3.8v12.6l-4.5-3.8h-3a1 1 0 0 1-1-1v-3a1 1 0 0 1 1-1z"/><path d="M15.5 9.2a4 4 0 0 1 0 5.6M18 6.8a7.5 7.5 0 0 1 0 10.4"/>',
  mute: '<path d="M4.5 9.5h3l4.5-3.8v12.6l-4.5-3.8h-3a1 1 0 0 1-1-1v-3a1 1 0 0 1 1-1z"/><path d="m16 9.5 5 5M21 9.5l-5 5"/>',
  fullscreen: '<path d="M4 9V5a1 1 0 0 1 1-1h4M15 4h4a1 1 0 0 1 1 1v4M20 15v4a1 1 0 0 1-1 1h-4M9 20H5a1 1 0 0 1-1-1v-4"/>',
  unfullscreen: '<path d="M9 4v4a1 1 0 0 1-1 1H4M20 9h-4a1 1 0 0 1-1-1V4M15 20v-4a1 1 0 0 1 1-1h4M4 15h4a1 1 0 0 1 1 1v4"/>',
  cc: '<rect x="3" y="5.5" width="18" height="13" rx="2.5"/><path d="M10.5 10.3a2 2 0 1 0 0 3.4M16.5 10.3a2 2 0 1 0 0 3.4"/>',
  list: '<path d="M9 6.5h11M9 12h11M9 17.5h11"/><circle cx="5" cy="6.5" r=".9" fill="currentColor"/><circle cx="5" cy="12" r=".9" fill="currentColor"/><circle cx="5" cy="17.5" r=".9" fill="currentColor"/>',
  grid: '<rect x="4" y="4" width="6.5" height="6.5" rx="1.5"/><rect x="13.5" y="4" width="6.5" height="6.5" rx="1.5"/><rect x="4" y="13.5" width="6.5" height="6.5" rx="1.5"/><rect x="13.5" y="13.5" width="6.5" height="6.5" rx="1.5"/>',
  close: '<path d="m6.5 6.5 11 11M17.5 6.5l-11 11"/>',
  filter: '<path d="M4 5.5h16l-6 7v5.5l-4 1.5v-7z"/>',
  sort: '<path d="M7.5 4.5v15M4.5 16.5l3 3 3-3M16.5 19.5v-15M13.5 7.5l3-3 3 3"/>',
  chevR: '<path d="m9.5 6 6 6-6 6"/>',
  chevL: '<path d="m14.5 6-6 6 6 6"/>',
  chevD: '<path d="m6 9.5 6 6 6-6"/>',
  check: '<path d="m5 12.5 4.5 4.5L19 7.5"/>',
  info: '<circle cx="12" cy="12" r="8.5"/><path d="M12 11v5.5"/><circle cx="12" cy="7.8" r=".7" fill="currentColor"/>',
  warning: '<path d="M10.3 4.8a2 2 0 0 1 3.4 0l7.2 12.4a2 2 0 0 1-1.7 3H4.8a2 2 0 0 1-1.7-3z"/><path d="M12 9.5v4.5"/><circle cx="12" cy="17" r=".7" fill="currentColor"/>',
  error: '<circle cx="12" cy="12" r="8.5"/><path d="m9 9 6 6M15 9l-6 6"/>',
  success: '<circle cx="12" cy="12" r="8.5"/><path d="m8.2 12.3 2.6 2.6 5-5.2"/>',
  refresh: '<path d="M19.5 12a7.5 7.5 0 1 1-2.2-5.3"/><path d="M19.5 4.5v4h-4"/>',
  star: '<path d="m12 3.8 2.5 5.1 5.6.8-4 4 1 5.6-5.1-2.7-5 2.7 1-5.6-4.1-4 5.6-.8z" fill="currentColor" stroke="none"/>',
  user: '<circle cx="12" cy="8.5" r="3.8"/><path d="M4.5 20a7.5 7.5 0 0 1 15 0"/>',
  server: '<rect x="4" y="4" width="16" height="7" rx="1.5"/><rect x="4" y="13" width="16" height="7" rx="1.5"/><circle cx="8" cy="7.5" r=".8" fill="currentColor"/><circle cx="8" cy="16.5" r=".8" fill="currentColor"/>',
  sun: '<circle cx="12" cy="12" r="4"/><path d="M12 3v2M12 19v2M3 12h2M19 12h2M5.6 5.6 7 7M17 17l1.4 1.4M5.6 18.4 7 17M17 7l1.4-1.4"/>',
  chip: '<rect x="6.5" y="6.5" width="11" height="11" rx="1.5"/><path d="M9.5 3.5v3M14.5 3.5v3M9.5 17.5v3M14.5 17.5v3M3.5 9.5h3M3.5 14.5h3M17.5 9.5h3M17.5 14.5h3"/>',
  display: '<rect x="3" y="4.5" width="18" height="12" rx="2"/><path d="M8.5 20h7M12 16.5V20"/><path d="m10.5 8.2 3.6 2.3-3.6 2.3z" fill="currentColor"/>',
  file: '<path d="M7 3.5h6.5L18 8v12a.5.5 0 0 1-.5.5h-10A.5.5 0 0 1 7 20z"/><path d="M13.5 3.5V8H18"/>',
  trash: '<path d="M5 7h14M10 4.5h4M7 7l.8 12a1 1 0 0 0 1 .9h6.4a1 1 0 0 0 1-.9L17 7"/>',
  external: '<path d="M14 4.5h5.5V10M19.5 4.5 11 13"/><path d="M18 14v4.5a1 1 0 0 1-1 1H6a1 1 0 0 1-1-1v-11a1 1 0 0 1 1-1h4.5"/>',
  logout: '<path d="M14 4.5h4a1.5 1.5 0 0 1 1.5 1.5v12a1.5 1.5 0 0 1-1.5 1.5h-4M10 16l-4-4 4-4M6 12h9"/>',
  audio: '<path d="M9 18V6.5l10-2V16"/><circle cx="6.8" cy="18" r="2.2"/><circle cx="16.8" cy="16" r="2.2"/>',
  layers: '<path d="m12 4 8.5 4.5L12 13 3.5 8.5z"/><path d="m3.5 12.5 8.5 4.5 8.5-4.5"/>',
  theme: '<circle cx="12" cy="12" r="8"/><path d="M12 4a8 8 0 0 1 0 16z" fill="currentColor" stroke="none"/>',
  book: '<path d="M5 4.5h10.5a2 2 0 0 1 2 2V20H7a2 2 0 0 1-2-2z"/><path d="M5 17.5a2 2 0 0 1 2-2h10.5"/>',
};
ICON.settings = (() => {
  let d = '';
  const teeth = 8, rOut = 8.8, rIn = 6.9;
  for (let k = 0; k < teeth; k++) {
    const a = k * Math.PI * 2 / teeth;
    const pts = [[a - .42, rIn], [a - .2, rOut], [a + .2, rOut], [a + .42, rIn]];
    for (const [ang, rr] of pts) d += `${d ? 'L' : 'M'}${f1(12 + Math.cos(ang) * rr)} ${f1(12 + Math.sin(ang) * rr)}`;
  }
  return `<path d="${d}Z"/><circle cx="12" cy="12" r="2.8"/>`;
})();
function icon(name, cls = '') {
  return `<svg class="ico ${cls}" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.6" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true">${ICON[name] || ''}</svg>`;
}
function capGlyph(kind) {
  const p = {
    min: '<path d="M.5 5.5h9"/>',
    max: '<rect x=".5" y=".5" width="9" height="9" rx="1.6"/>',
    restore: '<rect x=".5" y="2.5" width="7" height="7" rx="1.3"/><path d="M2.6 2.5v-.6A1.4 1.4 0 0 1 4 .5h4.1a1.4 1.4 0 0 1 1.4 1.4V6a1.4 1.4 0 0 1-1.4 1.4h-.6"/>',
    close: '<path d="m.6.6 8.8 8.8M9.4.6.6 9.4"/>',
  };
  return `<svg width="10" height="10" viewBox="0 0 10 10" fill="none" stroke="currentColor" stroke-width="1">${p[kind]}</svg>`;
}

/* ================= 程序化插画（替代海报与剧照） ================= */
const MOTIFS = ['planet', 'mountains', 'waves', 'city', 'dunes', 'aurora', 'forest', 'road'];
const MOTIF = {
  planet(W, H, h, r) {
    const cx = W * (0.56 + r() * 0.22), cy = H * (0.30 + r() * 0.22), R = Math.min(W, H) * (0.20 + r() * 0.12);
    let stars = '';
    for (let i = 0; i < 80; i++) stars += `<circle cx="${f1(r() * W)}" cy="${f1(r() * H)}" r="${f1(r() * 1.7 + .3)}" fill="#fff" opacity="${f1(r() * .7 + .15)}"/>`;
    return `<defs><linearGradient id="b" x1="0" y1="0" x2="1" y2="1"><stop offset="0" stop-color="${hsl(h, 48, 17)}"/><stop offset="1" stop-color="${hsl(h + 40, 55, 6)}"/></linearGradient>
      <radialGradient id="g" cx="${f1(cx / W * 100)}%" cy="${f1(cy / H * 100)}%" r="60%"><stop offset="0" stop-color="${hsl(h + 20, 85, 62)}" stop-opacity=".55"/><stop offset="1" stop-color="${hsl(h + 20, 85, 62)}" stop-opacity="0"/></radialGradient>
      <radialGradient id="p" cx="32%" cy="28%" r="85%"><stop offset="0" stop-color="${hsl(h + 15, 75, 74)}"/><stop offset=".55" stop-color="${hsl(h, 55, 40)}"/><stop offset="1" stop-color="${hsl(h - 25, 60, 10)}"/></radialGradient></defs>
      <rect width="${W}" height="${H}" fill="url(#b)"/><rect width="${W}" height="${H}" fill="url(#g)"/>${stars}
      <circle cx="${f1(cx)}" cy="${f1(cy)}" r="${f1(R)}" fill="url(#p)"/>
      <ellipse cx="${f1(cx)}" cy="${f1(cy)}" rx="${f1(R * 1.75)}" ry="${f1(R * .3)}" fill="none" stroke="${hsl(h + 35, 80, 82)}" stroke-opacity=".5" stroke-width="${f1(R * .035)}" transform="rotate(${f1(-8 - r() * 14)} ${f1(cx)} ${f1(cy)})"/>`;
  },
  mountains(W, H, h, r) {
    const sx = W * (.25 + r() * .5), sy = H * (.34 + r() * .14), sr = Math.min(W, H) * (.06 + r() * .04);
    let ridges = '';
    for (let i = 0; i < 4; i++) {
      const base = H * (.52 + i * .12), amp = H * (.17 - i * .025);
      const steps = 8 + i * 3;
      let d = `M0 ${H}L0 ${f1(base - r() * amp)}`;
      for (let s = 1; s <= steps; s++) d += `L${f1(W * s / steps)} ${f1(base - r() * amp)}`;
      ridges += `<path d="${d}L${W} ${H}Z" fill="${hsl(h - 15 + i * 4, 30 + i * 3, 34 - i * 7)}"/>`;
      if (i < 3) ridges += `<rect y="${f1(base - amp * .3)}" width="${W}" height="${f1(H - base + amp * .3)}" fill="url(#m)"/>`;
    }
    return `<defs><linearGradient id="s" x1="0" y1="0" x2="0" y2="1"><stop offset="0" stop-color="${hsl(h, 50, 24)}"/><stop offset=".55" stop-color="${hsl(h + 25, 62, 58)}"/><stop offset="1" stop-color="${hsl(h + 45, 85, 82)}"/></linearGradient>
      <radialGradient id="u"><stop offset="0" stop-color="${hsl(h + 50, 100, 92)}"/><stop offset=".35" stop-color="${hsl(h + 45, 95, 80)}" stop-opacity=".85"/><stop offset="1" stop-color="${hsl(h + 45, 95, 80)}" stop-opacity="0"/></radialGradient>
      <linearGradient id="m" x1="0" y1="0" x2="0" y2="1"><stop offset="0" stop-color="#fff" stop-opacity="0"/><stop offset="1" stop-color="#fff" stop-opacity=".28"/></linearGradient></defs>
      <rect width="${W}" height="${H}" fill="url(#s)"/><circle cx="${f1(sx)}" cy="${f1(sy)}" r="${f1(sr * 3.2)}" fill="url(#u)"/><circle cx="${f1(sx)}" cy="${f1(sy)}" r="${f1(sr)}" fill="${hsl(h + 50, 100, 95)}"/>${ridges}`;
  },
  waves(W, H, h, r) {
    let rays = '';
    for (let i = 0; i < 5; i++) {
      const x = W * (.15 + r() * .7), w = W * (.02 + r() * .04);
      rays += `<path d="M${f1(x - w)} 0L${f1(x + w)} 0L${f1(x + w * 3 + W * .08)} ${H}L${f1(x - w * 2 + W * .04)} ${H}Z" fill="#fff" opacity="${f1(.03 + r() * .05)}"/>`;
    }
    let waves = '';
    for (let i = 0; i < 6; i++) {
      const y0 = H * (.46 + i * .09), A = H * (.018 + i * .006), k = (1.3 + r() * 1.6) * Math.PI * 2 / W, ph = r() * 6.28;
      let d = `M0 ${H}L0 ${f1(y0 + Math.sin(ph) * A)}`;
      for (let x = W / 48; x <= W + 1; x += W / 48) d += `L${f1(x)} ${f1(y0 + Math.sin(k * x + ph) * A)}`;
      waves += `<path d="${d}L${W} ${H}Z" fill="${hsl(h + i * 3, 62, 34 - i * 4.5)}" opacity="${f1(.78 + i * .04)}"/>`;
    }
    let bubbles = '';
    for (let i = 0; i < 18; i++) bubbles += `<circle cx="${f1(r() * W)}" cy="${f1(H * (.5 + r() * .5))}" r="${f1(1 + r() * 4)}" fill="none" stroke="#fff" stroke-opacity=".25"/>`;
    return `<defs><linearGradient id="d" x1="0" y1="0" x2="0" y2="1"><stop offset="0" stop-color="${hsl(h, 58, 40)}"/><stop offset="1" stop-color="${hsl(h + 20, 70, 8)}"/></linearGradient></defs><rect width="${W}" height="${H}" fill="url(#d)"/>${rays}${waves}${bubbles}`;
  },
  city(W, H, h, r) {
    const mx = W * (.15 + r() * .7), my = H * (.16 + r() * .14), mr = Math.min(W, H) * .05;
    let back = '', front = '', wins = '';
    let x = 0;
    while (x < W) { const w = W * (.04 + r() * .06), hh = H * (.22 + r() * .28); back += `<rect x="${f1(x)}" y="${f1(H - hh - H * .08)}" width="${f1(w)}" height="${f1(hh + H * .08)}" fill="${hsl(h, 30, 20)}"/>`; x += w + W * .004; }
    x = 0;
    while (x < W) {
      const w = W * (.05 + r() * .08), hh = H * (.14 + r() * .3), top = H - hh;
      front += `<rect x="${f1(x)}" y="${f1(top)}" width="${f1(w)}" height="${f1(hh)}" fill="${hsl(h, 32, 9)}"/>`;
      for (let wy = top + H * .02; wy < H - H * .03; wy += H * .035)
        for (let wx = x + w * .15; wx < x + w * .85; wx += w * .22)
          if (r() < .22) wins += `<rect x="${f1(wx)}" y="${f1(wy)}" width="${f1(w * .09)}" height="${f1(H * .014)}" fill="${hsl(42, 95, 72)}" opacity="${f1(.5 + r() * .5)}"/>`;
      x += w + W * .006;
    }
    return `<defs><linearGradient id="k" x1="0" y1="0" x2="0" y2="1"><stop offset="0" stop-color="${hsl(h, 55, 12)}"/><stop offset=".7" stop-color="${hsl(h + 30, 60, 32)}"/><stop offset="1" stop-color="${hsl(h + 50, 70, 48)}"/></linearGradient>
      <radialGradient id="g"><stop offset="0" stop-color="${hsl(h + 40, 60, 92)}" stop-opacity=".6"/><stop offset="1" stop-color="${hsl(h + 40, 60, 92)}" stop-opacity="0"/></radialGradient></defs>
      <rect width="${W}" height="${H}" fill="url(#k)"/><circle cx="${f1(mx)}" cy="${f1(my)}" r="${f1(mr * 3)}" fill="url(#g)"/><circle cx="${f1(mx)}" cy="${f1(my)}" r="${f1(mr)}" fill="${hsl(h + 40, 60, 92)}"/>${back}${front}${wins}`;
  },
  dunes(W, H, h0, r) {
    const h = 16 + (h0 % 28);
    const sx = W * (.3 + r() * .4), sy = H * (.3 + r() * .1), sr = Math.min(W, H) * .11;
    let dunes = '';
    for (let i = 0; i < 5; i++) {
      const y = H * (.52 + i * .1), c1 = W * (.2 + r() * .3);
      dunes += `<path d="M0 ${f1(y + H * .05)}Q${f1(c1)} ${f1(y - H * (.08 + r() * .06))} ${f1(W * .5)} ${f1(y)}T${W} ${f1(y - H * .03)}L${W} ${H}L0 ${H}Z" fill="${hsl(h + i * 2, 62 - i * 4, 58 - i * 8)}"/>`;
    }
    return `<defs><linearGradient id="k" x1="0" y1="0" x2="0" y2="1"><stop offset="0" stop-color="${hsl(h + 200, 45, 30)}"/><stop offset=".6" stop-color="${hsl(h + 10, 75, 66)}"/><stop offset="1" stop-color="${hsl(h + 25, 90, 82)}"/></linearGradient></defs>
      <rect width="${W}" height="${H}" fill="url(#k)"/><circle cx="${f1(sx)}" cy="${f1(sy)}" r="${f1(sr)}" fill="${hsl(h + 30, 100, 90)}" opacity=".95"/>${dunes}`;
  },
  aurora(W, H, h, r) {
    let defs = '', blobs = '', stars = '';
    for (let i = 0; i < 5; i++) {
      const hue = h + i * 38;
      defs += `<radialGradient id="a${i}"><stop offset="0" stop-color="${hsl(hue, 80, 62)}" stop-opacity=".85"/><stop offset="1" stop-color="${hsl(hue, 80, 62)}" stop-opacity="0"/></radialGradient>`;
      blobs += `<ellipse cx="${f1(r() * W)}" cy="${f1(r() * H * .8)}" rx="${f1(W * (.25 + r() * .25))}" ry="${f1(H * (.18 + r() * .2))}" fill="url(#a${i})"/>`;
    }
    for (let i = 0; i < 40; i++) stars += `<circle cx="${f1(r() * W)}" cy="${f1(r() * H * .6)}" r="${f1(r() * 1.2 + .3)}" fill="#fff" opacity="${f1(r() * .6 + .2)}"/>`;
    return `<defs>${defs}</defs><rect width="${W}" height="${H}" fill="${hsl(h + 200, 45, 8)}"/>${stars}${blobs}
      <path d="M0 ${f1(H * .82)}Q${f1(W * .3)} ${f1(H * .76)} ${f1(W * .6)} ${f1(H * .83)}T${W} ${f1(H * .8)}L${W} ${H}L0 ${H}Z" fill="${hsl(h, 30, 7)}"/>`;
  },
  forest(W, H, h, r) {
    let layers = '';
    for (let i = 0; i < 4; i++) {
      const base = H * (.58 + i * .11);
      let trees = '';
      for (let x = -20; x < W + 20; x += W * (.025 + r() * .03)) {
        const th = H * (.12 + r() * .12) * (1 - i * .1), tw = th * .42;
        trees += `<path d="M${f1(x)} ${f1(base - th)}L${f1(x + tw / 2)} ${f1(base)}L${f1(x - tw / 2)} ${f1(base)}Z"/>`;
      }
      layers += `<g fill="${hsl(h + 120 + i * 3, 22 + i * 5, 32 - i * 7)}">${trees}<rect y="${f1(base - 1)}" width="${W}" height="${f1(H - base + 1)}"/></g><rect y="${f1(base - H * .1)}" width="${W}" height="${f1(H * .14)}" fill="url(#f)"/>`;
    }
    return `<defs><linearGradient id="s" x1="0" y1="0" x2="0" y2="1"><stop offset="0" stop-color="${hsl(h + 180, 30, 66)}"/><stop offset="1" stop-color="${hsl(h + 140, 25, 88)}"/></linearGradient>
      <linearGradient id="f" x1="0" y1="0" x2="0" y2="1"><stop offset="0" stop-color="#fff" stop-opacity="0"/><stop offset="1" stop-color="#fff" stop-opacity=".3"/></linearGradient></defs>
      <rect width="${W}" height="${H}" fill="url(#s)"/>${layers}`;
  },
  road(W, H, h, r) {
    const hy = H * .58, vx = W * (.4 + r() * .2);
    let stripes = '';
    for (let i = 0; i < 6; i++) {
      const t0 = i / 6 + .02, t1 = t0 + .06;
      const y0 = hy + (H - hy) * t0 * t0, y1 = hy + (H - hy) * t1 * t1;
      const w0 = W * .003 + W * .018 * t0 * t0, w1 = W * .003 + W * .018 * t1 * t1;
      stripes += `<path d="M${f1(vx - w0)} ${f1(y0)}L${f1(vx + w0)} ${f1(y0)}L${f1(vx + w1)} ${f1(y1)}L${f1(vx - w1)} ${f1(y1)}Z" fill="${hsl(h + 40, 90, 85)}" opacity=".8"/>`;
    }
    return `<defs><linearGradient id="k" x1="0" y1="0" x2="0" y2="1"><stop offset="0" stop-color="${hsl(h + 250, 50, 22)}"/><stop offset=".75" stop-color="${hsl(h + 330, 70, 55)}"/><stop offset="1" stop-color="${hsl(h + 20, 90, 70)}"/></linearGradient>
      <radialGradient id="u"><stop offset="0" stop-color="${hsl(h + 40, 100, 85)}"/><stop offset=".6" stop-color="${hsl(h + 20, 95, 65)}" stop-opacity=".8"/><stop offset="1" stop-color="${hsl(h + 20, 95, 65)}" stop-opacity="0"/></radialGradient>
      <linearGradient id="g" x1="0" y1="0" x2="0" y2="1"><stop offset="0" stop-color="${hsl(h + 260, 40, 16)}"/><stop offset="1" stop-color="${hsl(h + 250, 45, 6)}"/></linearGradient></defs>
      <rect width="${W}" height="${f1(hy)}" fill="url(#k)"/><circle cx="${f1(vx)}" cy="${f1(hy)}" r="${f1(Math.min(W, H) * .17)}" fill="url(#u)"/>
      <rect y="${f1(hy)}" width="${W}" height="${f1(H - hy)}" fill="url(#g)"/>
      <path d="M${f1(vx - W * .003)} ${f1(hy)}L${f1(vx + W * .003)} ${f1(hy)}L${f1(vx + W * .35)} ${H}L${f1(vx - W * .35)} ${H}Z" fill="${hsl(h + 255, 25, 12)}"/>${stripes}`;
  },
};
const ART_CACHE = new Map();
function art(seed, ratio = 'wide', motif = null) {
  const key = `${seed}|${ratio}|${motif}`;
  if (ART_CACHE.has(key)) return ART_CACHE.get(key);
  const pick = rng(seed);
  const h = Math.floor(pick() * 360);
  const m = motif || MOTIFS[Math.floor(pick() * MOTIFS.length)];
  const [W, H] = ratio === 'poster' ? [600, 900] : ratio === 'square' ? [600, 800] : [1600, 900];
  const body = MOTIF[m](W, H, h, rng(`${seed}|${ratio}`));
  const svg = `<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 ${W} ${H}" preserveAspectRatio="xMidYMid slice">${body}</svg>`;
  const url = `url("data:image/svg+xml;charset=utf-8,${encodeURIComponent(svg)}")`;
  ART_CACHE.set(key, url);
  return url;
}

/* ================= 演示数据（全部虚构） ================= */
const GENRES = ['剧情', '科幻', '悬疑', '冒险', '爱情', '动画', '纪录', '喜剧', '惊悚', '家庭'];
const CERTS = ['PG', 'PG-13', 'R', 'TV-14'];
const OVERVIEWS = [
  '一座终年被雾笼罩的港口小城里，退休的灯塔看守人收到一封寄自三十年前的信，信中写着一个从未发生过的约定。',
  '为了寻找失踪的妹妹，年轻的地质学家独自深入沙漠腹地，却发现那里埋藏着一座会随风迁移的城市。',
  '深空探测船在返航途中接收到一段重复的求救信号，信号里的声纹与船员们自己的声音完全一致。',
  '两位素不相识的旅人在雪夜的末班车上相遇，十二个小时的旅程，改变了他们各自余下的人生。',
  '小镇钟表匠能让时间倒退三分钟，他用这项能力修补了无数遗憾，直到某天时间不再听他的话。',
  '一档深夜电台节目每晚都会接到同一位听众的来电，她讲述的故事，总会在第二天变成现实。',
  '海底观测站的研究员在例行巡检中发现了一扇本不该存在的门，门后传来熟悉的歌声。',
  '退役的赛车手接下最后一份工作：驾车横穿大陆，把一个不肯开口的孩子送到海的另一边。',
  '城市边缘的旧电影院即将拆除，放映员决定在最后一夜，放完那部从未公映过的胶片。',
  '山村教师带着六个孩子徒步翻越雪山去参加比赛，路上的每一天都比终点更重要。',
  '一位失去记忆的画家每天醒来都会看到墙上新增的一幅画，画中的地点正一步步向他靠近。',
  '星际货运公司的新人在第一次航行中迷失在小行星带，只能依靠一台老旧的导航机器人回家。',
];
const MOVIE_TITLES = ['长夜将尽', '雾港来信', '最后的灯塔', '北纬三十度', '纸飞机', '无声的河', '余温', '远山回响', '风起之时', '白昼之月',
  '蓝色时刻', '迷途者', '夏日终章', '潮汐之间', '孤岛电台', '晚星', '回声谷', '逆光而行', '第七个冬天', '沙丘之歌', '城市边缘', '星光旅馆', '微光', '归途',
  '星河彼岸', '流星雨后', '星期八', '追星的人'];
const SHORT_TITLES = ['晨光小品', '雨夜电车', '猫与旧书店', '一分钟的海', '星空露营', '纸灯笼', '屋顶花园', '雪落无声', '远行之前', '风铃', '夜航'];
const PEOPLE = [
  { id: 'p1', name: '林知远', kind: '演员', role: '饰 沈默' }, { id: 'p2', name: '许晴川', kind: '演员', role: '饰 苏念' },
  { id: 'p3', name: '周默', kind: '演员', role: '饰 老陈' }, { id: 'p4', name: '沈一禾', kind: '演员', role: '饰 小禾' },
  { id: 'p5', name: '叶青', kind: '演员', role: '饰 船长' }, { id: 'p6', name: '陈屿', kind: '导演', role: '导演' },
  { id: 'p7', name: '顾南星', kind: '编剧', role: '编剧' }, { id: 'p8', name: '唐小满', kind: '制片', role: '制片' },
];

const ITEMS = new Map();
const MOVIES = MOVIE_TITLES.map((title, i) => {
  const r = rng('movie' + i);
  const g1 = GENRES[Math.floor(r() * GENRES.length)];
  let g2 = GENRES[Math.floor(r() * GENRES.length)]; if (g2 === g1) g2 = GENRES[(GENRES.indexOf(g1) + 3) % GENRES.length];
  const m = { id: 'm' + (i + 1), kind: 'movie', lib: 'movies', title, year: 2012 + Math.floor(r() * 14), rating: (6.6 + r() * 2.6).toFixed(1),
    genres: [g1, g2], cert: CERTS[Math.floor(r() * CERTS.length)], runtime: 88 + Math.floor(r() * 60), overview: OVERVIEWS[i % OVERVIEWS.length],
    progress: [1, 5, 9, 14].includes(i) ? .12 + r() * .7 : 0, played: [3, 11, 17].includes(i), logo: i % 3 };
  ITEMS.set(m.id, m); return m;
});
const EXTRA_MOVIES = Array.from({ length: 20 }, (_, k) => {
  const i = MOVIES.length + k + 1, r = rng('extra' + i);
  const m = { id: 'm' + i, kind: 'movie', lib: 'movies', title: `${MOVIE_TITLES[k % 20].slice(0, 2)}${['之歌', '往事', '来客', '之城', '旅人', '序曲'][k % 6]}`,
    year: 2010 + Math.floor(r() * 16), rating: (6.4 + r() * 2.8).toFixed(1), genres: [GENRES[i % 10], GENRES[(i + 4) % 10]], cert: CERTS[i % 4],
    runtime: 90 + Math.floor(r() * 50), overview: OVERVIEWS[i % 12], progress: 0, played: false, logo: i % 3 };
  ITEMS.set(m.id, m); return m;
});
const allMovies = () => MOVIES.concat(EXTRA_MOVIES);
const SHORTS = SHORT_TITLES.map((title, i) => {
  const r = rng('short' + i);
  const s = { id: 'v' + (i + 1), kind: 'video', lib: 'shorts', title, year: 2019 + Math.floor(r() * 7), rating: (6.8 + r() * 2).toFixed(1),
    genres: ['短片', GENRES[Math.floor(r() * GENRES.length)]], cert: 'PG', runtime: 4 + Math.floor(r() * 18), overview: OVERVIEWS[(i + 5) % OVERVIEWS.length], progress: 0, played: false, logo: 1 };
  ITEMS.set(s.id, s); return s;
});
const EP_NAMES = {
  s1: ['启航', '静默信号', '回声之海', '失重', '双星', '裂隙', '远航日志', '碎冰带', '归零', '暗面', '重逢', '回声',
    '新航线', '冷光', '环带', '孤舟', '引力井', '余烬', '信标', '雾面', '回航', '星图', '断层', '黎明',
    '远日点', '镜像', '潮汐锁定', '长夜', '边界', '星尘', '深渊', '微光', '彼岸', '旧信', '灯塔', '终章'],
  s2: ['雪线', '盐田', '渔火', '竹海', '古道', '石屋', '风车', '茶山', '海市', '冰川', '梯田', '归港'],
  s3: ['门', '回声', '第七层', '失联', '深蓝', '来信', '观测站', '暗流', '歌声', '浮标', '沉船', '水面'],
};
const SERIES = [
  { id: 's1', kind: 'series', lib: 'shows', title: '星际回声', motif: 'planet', year: 2024, rating: '8.9', genres: ['科幻', '冒险', '剧情'], cert: 'TV-14', seasons: 3, overview: OVERVIEWS[2], logo: 1 },
  { id: 's2', kind: 'series', lib: 'shows', title: '山海旅途', motif: 'mountains', year: 2023, rating: '9.1', genres: ['纪录', '冒险'], cert: 'TV-PG', seasons: 2, overview: '摄制组用三年时间走过九座山脉与七片海岸，记录那些仍在坚守古老手艺的人。', logo: 0 },
  { id: 's3', kind: 'series', lib: 'shows', title: '深海来信', motif: 'waves', year: 2025, rating: '8.4', genres: ['悬疑', '剧情'], cert: 'TV-14', seasons: 1, overview: OVERVIEWS[6], logo: 2 },
];
const EPISODES = new Map();
for (const s of SERIES) {
  ITEMS.set(s.id, s);
  const names = EP_NAMES[s.id];
  for (let season = 1; season <= s.seasons; season++) {
    const list = [];
    for (let n = 1; n <= 12; n++) {
      const r = rng(`${s.id}-${season}-${n}`);
      const idx = (season - 1) * 12 + n - 1;
      const ep = { id: `${s.id}e${season}x${n}`, kind: 'episode', lib: 'shows', seriesId: s.id, seriesTitle: s.title, season, number: n, motif: s.motif,
        title: names[idx] || `${['潮汐', '微光', '边界', '余烬', '航线', '迷雾'][idx % 6]} ${n}`, runtime: 42 + Math.floor(r() * 9),
        date: `20${s.year % 100 + season - 1}-0${1 + Math.floor((n - 1) / 4)}-${pad2(3 + ((n - 1) % 4) * 7)}`,
        overview: OVERVIEWS[(idx + 3) % OVERVIEWS.length], played: false, progress: 0 };
      list.push(ep); ITEMS.set(ep.id, ep);
    }
    EPISODES.set(`${s.id}:${season}`, list);
  }
}
for (const [id, p] of [['s1e1x1', 1], ['s1e1x2', 1], ['s1e1x3', .42], ['s2e1x1', 1], ['s2e1x2', 1], ['s2e1x3', 1], ['s2e1x4', 1], ['s2e1x5', .6], ['s3e1x1', 1], ['s3e1x2', .78]]) {
  const ep = ITEMS.get(id); if (p >= 1) ep.played = true; else ep.progress = p;
}
// 再给几部电影一些播放进度，让"最近播放"页更接近真实情况。
for (const [id, p] of [['m5', .31], ['m8', .57], ['m13', .22], ['m20', .68], ['m23', .44]]) ITEMS.get(id).progress = p;
const LIBS = [
  { id: 'movies', name: '电影', icon: 'movie', count: 5000, items: allMovies },
  { id: 'shows', name: '剧集', icon: 'tv', count: 3, items: () => SERIES },
  { id: 'shorts', name: '短片', icon: 'folder', count: 24, items: () => SHORTS },
];
const HERO = ['s1', 'm1', 's2', 'm7', 'm16', 's3'].map(id => ITEMS.get(id));
const RESUME = ['s1e1x3', 'm2', 's2e1x5', 'm6', 's3e1x2', 'm10', 'm15'].map(id => ITEMS.get(id));
const motifOf = item => item.motif || (item.seriesId ? ITEMS.get(item.seriesId).motif : null);
const progressOf = item => item.progress > 0 && item.progress < 1 ? item.progress : 0;
function nextUp(series) {
  const eps = [];
  for (let season = 1; season <= series.seasons; season++) eps.push(...EPISODES.get(`${series.id}:${season}`));
  return eps.find(e => e.progress > 0) || eps.find(e => !e.played) || eps[0];
}
function seasonPlan(ep) { return EPISODES.get(`${ep.seriesId}:${ep.season}`); }

/* ================= 状态 ================= */
const S = {
  route: { name: 'home', params: {} }, back: [], fwd: [],
  loggedIn: true, homeVariant: 'normal', libVariant: 'normal',
  theme: 'light', themeMode: 'system', size: 'large', fit: true, reduceMotion: false, annotate: false, scale: 1,
  heroIndex: 0, heroHover: false,
  filtersOpen: false, sortOpen: false, sort: 'added', filters: { genre: new Set(), decade: new Set(), cert: new Set() },
  searchText: '', season: {}, selectedEp: {},
  connecting: false, playbackMode: 'embedded', mpvPath: '', mpvStatus: 'none', hdr: 'auto', hwdec: true, hdrMenu: false,
  player: null, dialog: null, toasts: [], snapOpen: false,
};
const win = () => $('#window');
const host = () => $('#page');
const systemDark = window.matchMedia('(prefers-color-scheme: dark)');
const systemReducedMotion = window.matchMedia('(prefers-reduced-motion: reduce)');
const effectiveReduced = () => S.reduceMotion || systemReducedMotion.matches;
function applyMotionPreference() {
  const reduced = effectiveReduced(), w = win();
  if (w.classList.contains('reduce-motion') === reduced) return;
  w.classList.toggle('reduce-motion', reduced);
  if (reduced) {
    S._entering = false;
    if (pendingPlayerClose) finishPlayerClose(pendingPlayerClose);
    else if (S.player) $('#player').classList.add('open');
    for (const el of [host(), ...w.querySelectorAll('.rail-track')]) {
      el.scrollTo({ left: el.scrollLeft, top: el.scrollTop, behavior: 'instant' });
    }
  }
  // Removing the reduced rule must not replay animations on existing content.
  // Later state changes and newly rendered nodes can animate normally.
  for (const animation of w.getAnimations({ subtree: true })) animation.cancel();
  resetHeroTimer();
}
systemReducedMotion.addEventListener('change', applyMotionPreference);
const resolveTheme = () => S.themeMode === 'system' ? (systemDark.matches ? 'dark' : 'light') : S.themeMode;
S.theme = resolveTheme();
systemDark.addEventListener('change', () => { if (S.themeMode === 'system') { S.theme = resolveTheme(); render({ page: false }); } });

/* ================= 导航 ================= */
function sameRoute(a, b) { return a.name === b.name && JSON.stringify(a.params || {}) === JSON.stringify(b.params || {}); }
function nav(name, params = {}, opts = {}) {
  const next = { name, params };
  if (sameRoute(S.route, next)) return;
  if (name === 'search' && S.route.name === 'search') opts.replace = true;
  if (!opts.replace) {
    S.back.push({ ...S.route, scroll: host().scrollTop });
    if (S.back.length > 12) S.back.shift();
    S.fwd = [];
  }
  S.route = next; S.sortOpen = false; S.hdrMenu = false;
  render({ scroll: 0, enter: true });
}
function goBack() {
  if (S.dialog) return closeDialog();
  if (S.player) return requestClosePlayer();
  const prev = S.back.pop(); if (!prev) return;
  S.fwd.push({ ...S.route, scroll: host().scrollTop }); if (S.fwd.length > 12) S.fwd.shift();
  S.route = { name: prev.name, params: prev.params };
  render({ scroll: prev.scroll || 0, enter: true });
}
function goForward() {
  if (S.player) return;
  const next = S.fwd.pop(); if (!next) return;
  S.back.push({ ...S.route, scroll: host().scrollTop });
  S.route = { name: next.name, params: next.params };
  render({ scroll: next.scroll || 0, enter: true });
}

/* ================= 外壳 ================= */
function renderTitlebar() {
  const p = S.player;
  let center = '';
  if (p) {
    const e = p.entries[p.index];
    center = `<div class="tb-title">${esc(e.seriesTitle ? `${e.seriesTitle} · 第 ${e.number} 集 ${e.title}` : e.title)}</div>`;
  } else if (S.route.name === 'home' && S.loggedIn && S.homeVariant === 'normal') {
    center = `<div class="hero-dots"><button class="tb-btn" data-act="hero-prev" aria-label="上一张">${icon('chevL', 'ico-14')}</button>
      ${HERO.map((_, i) => `<button class="dot-btn" data-act="hero-go" data-i="${i}" aria-label="第 ${i + 1} 张"><i class="dot ${i === S.heroIndex ? 'on' : ''}"></i></button>`).join('')}
      <button class="tb-btn" data-act="hero-next" aria-label="下一张">${icon('chevR', 'ico-14')}</button></div>`;
  } else if (S.route.name === 'detail') {
    const it = ITEMS.get(S.route.params.id);
    center = `<div class="tb-title">${esc(it.seriesTitle || it.title)}</div>`;
  }
  $('#titlebar').innerHTML = `
    <div class="tb-left">
      <button class="tb-btn" data-act="back" ${S.back.length || p ? '' : 'disabled'} aria-label="后退" title="后退（Alt+←）">${icon('back')}</button>
      <button class="tb-btn" data-act="forward" ${S.fwd.length && !p ? '' : 'disabled'} aria-label="前进" title="前进（Alt+→）">${icon('forward')}</button>
    </div>
    <div class="tb-center">${center}</div>
    <div class="tb-right">
      <button class="cap-btn" data-act="noop" title="最小化">${capGlyph('min')}</button>
      <button class="cap-btn anno-host" data-act="snap" data-d="D1" title="最大化">${capGlyph('max')}</button>
      <button class="cap-btn cap-close" data-act="win-close" title="关闭">${capGlyph('close')}</button>
      ${S.snapOpen ? `<div class="snap-flyout">${[['1fr 1fr', 2], ['2fr 1fr', 2], ['1fr 1fr 1fr', 3]].map(([cols, n]) =>
        `<div class="snap-cell" style="grid-template-columns:${cols}">${'<i></i>'.repeat(n)}</div>`).join('')}</div>` : ''}
    </div>`;
}

function renderSidebar() {
  const r = S.route.name, lib = S.route.params.lib;
  const item = (act, ico, label, active, extra = '') => `<button class="nav-item ${active ? 'active' : ''}" data-act="${act}" ${extra}>${icon(ico)}<span>${label}</span></button>`;
  $('#sidebar').innerHTML = `
    <div class="sb-search">${icon('search')}
      <input id="search-input" placeholder="${S.loggedIn ? '搜索电影、剧集' : '连接服务器后可用'}" value="${esc(S.searchText)}" ${S.loggedIn ? '' : 'disabled'} aria-label="搜索">
      ${S.searchText ? `<button class="sb-clear" data-act="search-clear" aria-label="清除">${icon('close', 'ico-14')}</button>` : ''}
    </div>
    ${item('go-home', 'home', '首页', r === 'home')}
    ${item('go-recent', 'recent', '最近播放', r === 'recent', S.loggedIn ? '' : 'disabled title="连接服务器后可用"')}
    <div class="sb-scroll">
      ${S.loggedIn ? `<div class="nav-section">媒体库</div>${LIBS.map(l => item('go-lib', l.icon, l.name, r === 'library' && lib === l.id, `data-lib="${l.id}"`)).join('')}` : ''}
    </div>
    ${item('go-settings', 'settings', '设置', r === 'settings')}`;
}

/* ================= 卡片 ================= */
function posterCard(item) {
  const sub = item.kind === 'series' ? `${item.year} · ${item.seasons} 季` : item.played ? `${item.year} · 已看完` : progressOf(item) ? `${item.year} · 已看 ${Math.round(item.progress * 100)}%` : `${item.year} · ${item.genres[0]}`;
  return `<div class="card card-poster" data-act="detail" data-id="${item.id}" role="button" tabindex="0">
    <div class="card-art" style='background-image:${art(item.id, 'poster', motifOf(item))}'>
      <span class="badge-rating">${icon('star')}${item.rating}</span>
      ${item.played ? `<span class="badge-played" title="已看完">${icon('check')}</span>` : ''}
      ${progressOf(item) ? `<div class="progress"><i style="width:${progressOf(item) * 100}%"></i></div>` : ''}
    </div>
    <div class="card-text"><div class="card-title">${esc(item.title)}</div><div class="card-sub">${esc(sub)}</div></div>
  </div>`;
}
function landscapeCard(item, opts = {}) {
  const isEp = item.kind === 'episode';
  const title = isEp ? item.seriesTitle : item.title;
  const left = item.runtime * (1 - progressOf(item));
  const sub = isEp ? `S${pad2(item.season)}E${pad2(item.number)} · ${item.title}` : progressOf(item) ? `剩余 ${fmtMinutes(Math.round(left))}` : `${item.year} · ${item.genres[0]}`;
  const target = isEp ? item.seriesId : item.id;
  return `<div class="card card-landscape" data-act="detail" data-id="${target}" role="button" tabindex="0">
    <div class="card-art" style='background-image:${art(item.id, 'wide', motifOf(item))}'>
      ${progressOf(item) ? `<div class="progress"><i style="width:${progressOf(item) * 100}%"></i></div>` : ''}
      <button class="card-play" data-act="play" data-id="${item.id}" aria-label="从这里播放" title="从这里播放">${icon('play')}</button>
    </div>
    <div class="card-text"><div class="card-title">${opts.mark ? markText(title, opts.mark) : esc(title)}</div><div class="card-sub">${esc(sub)}</div></div>
  </div>`;
}
function episodeCard(ep, selected) {
  return `<div class="card card-landscape ${selected ? 'selected' : ''}" data-act="select-ep" data-id="${ep.id}" role="button" tabindex="0">
    <div class="card-art" style='background-image:${art(ep.id, 'wide', ep.motif)}'>
      ${ep.played ? `<span class="badge-played" title="已看完">${icon('check')}</span>` : ''}
      ${progressOf(ep) ? `<div class="progress"><i style="width:${progressOf(ep) * 100}%"></i></div>` : ''}
      <button class="card-play" data-act="play" data-id="${ep.id}" aria-label="播放">${icon('play')}</button>
    </div>
    <div class="card-text"><div class="card-title">${ep.number}. ${esc(ep.title)}</div><div class="card-sub">${ep.date} · ${ep.runtime} 分钟</div></div>
  </div>`;
}
function markText(text, q) { const i = text.indexOf(q); return i < 0 ? esc(text) : `${esc(text.slice(0, i))}<mark>${esc(q)}</mark>${esc(text.slice(i + q.length))}`; }
function rail(title, cards, opts = {}) {
  return `<section class="rail">
    <div class="rail-head"><h2 class="rail-title">${esc(title)}</h2>${opts.link ? `<button class="rail-link" data-act="${opts.link.act}" ${opts.link.attrs || ''}>查看全部${icon('chevR', 'ico-14')}</button>` : ''}</div>
    <button class="rail-arrow left" data-act="rail-scroll" data-dir="-1" aria-label="向左">${icon('chevL')}</button>
    <div class="rail-track">${cards}</div>
    <button class="rail-arrow right" data-act="rail-scroll" data-dir="1" aria-label="向右">${icon('chevR')}</button>
  </section>`;
}

/* ================= 页面：首页 ================= */
function logoHtml(item) { return `<div class="hero-logo logo-s${item.logo ?? 0}">${esc(item.title)}</div>`; }
function metaRow(item) {
  const parts = [`<span class="star">${icon('star', 'ico-14')}${item.rating}</span>`, `<span>${item.year}</span>`, `<span>${item.genres.slice(0, 3).join(' / ')}</span>`];
  if (item.kind === 'series') parts.push(`<span>${item.seasons} 季</span>`);
  else if (item.runtime) parts.push(`<span>${fmtMinutes(item.runtime)}</span>`);
  return `<div class="meta-row">${parts.join('<i class="meta-sep"></i>')}<span class="badge-cert">${item.cert}</span></div>`;
}
function heroHtml(item, enter = false) {
  return `<section class="hero" data-hero="${item.id}">
    <div class="hero-art" style='background-image:${art(item.id, 'wide', motifOf(item))}'></div>
    <div class="hero-scrim"></div>
    <div class="hero-click" data-act="detail" data-id="${item.id}"></div>
    <div class="hero-content${enter && !effectiveReduced() ? ' enter' : ''}">
      ${logoHtml(item)}
      ${metaRow(item)}
      <p class="hero-overview">${esc(item.overview)}</p>
    </div>
  </section>`;
}
function pageHome() {
  if (!S.loggedIn) return pageOnboard();
  if (S.homeVariant === 'loading') return homeSkeleton();
  if (S.homeVariant === 'error') return homeError();
  const latest = LIBS.map(l => rail(`最新 · ${l.name}`, l.items().slice(0, 16).map(i => posterCard(i)).join(''), { link: { act: 'go-lib', attrs: `data-lib="${l.id}"` } })).join('');
  return `<div class="home">${heroHtml(HERO[S.heroIndex])}
    <div class="rails">${rail('最近播放', RESUME.map(i => landscapeCard(i)).join(''), { link: { act: 'go-recent' } })}${latest}</div></div>`;
}
function homeSkeleton() {
  const posters = n => Array.from({ length: n }, () => `<div class="card card-poster"><div class="skel" style="height:var(--poster-h);border-radius:var(--poster-r)"></div><div class="skel skel-line" style="width:70%"></div><div class="skel skel-line" style="width:44%;height:10px"></div></div>`).join('');
  const lands = n => Array.from({ length: n }, () => `<div class="card card-landscape"><div class="skel" style="height:var(--landscape-h);border-radius:var(--landscape-r)"></div><div class="skel skel-line" style="width:60%"></div></div>`).join('');
  return `<div class="home"><section class="hero"><div class="skel" style="position:absolute;inset:0;border-radius:0"></div>
      <div class="hero-content"><div class="skel" style="width:320px;height:64px;border-radius:10px;opacity:.7"></div><div class="skel skel-line" style="width:260px;opacity:.7"></div><div class="skel skel-line" style="width:420px;opacity:.7"></div></div></section>
    <div class="rails"><section class="rail"><div class="rail-head"><div class="skel skel-line" style="width:120px;height:18px"></div></div><div class="rail-track">${lands(5)}</div></section>
    <section class="rail"><div class="rail-head"><div class="skel skel-line" style="width:120px;height:18px"></div></div><div class="rail-track">${posters(8)}</div></section></div></div>`;
}
function homeError() {
  return `<div class="state">${stateIcon('server')}<h3 class="state-title">无法连接服务器</h3>
      <div class="state-actions"><button class="btn btn-accent" data-act="retry-home">${icon('refresh')}重试</button><button class="btn" data-act="go-settings">打开设置</button></div></div>`;
}
const stateIcon = name => `<div class="state-icon">${icon(name)}</div>`;
function pageOnboard() {
  return `<div class="onboard">
    <h1 class="onboard-title" data-act="go-settings"><span class="line">前往连接你的</span><span class="line"><span class="accent">Emby</span>&nbsp;服务器</span></h1></div>`;
}

/* ================= 页面：最近播放、资料库、搜索 ================= */
function pageHead(eyebrow, title, count, actions = '') {
  return `<div class="page-head"><div><div class="eyebrow">${eyebrow}</div><h1 class="page-title">${esc(title)}</h1></div>${count != null ? `<span class="count-pill">${count}</span>` : ''}${actions ? `<div class="page-actions">${actions}</div>` : ''}</div>`;
}
function pageRecent() {
  const list = [...RESUME, ...[...ITEMS.values()].filter(i => (i.kind === 'episode' || i.kind === 'movie') && progressOf(i) && !RESUME.includes(i))];
  return `<div class="page">${pageHead('RECENT', '最近播放', `${list.length} 项`)}<div class="grid grid-landscape">${list.map(i => landscapeCard(i)).join('')}</div></div>`;
}
const SORTS = [['added', '添加日期'], ['name', '名称'], ['rating', '评分'], ['year', '年份'], ['runtime', '时长']];
function filterCount() { return S.filters.genre.size + S.filters.decade.size + S.filters.cert.size; }
function libraryItems(lib) {
  let list = [...lib.items()];
  const f = S.filters;
  list = list.filter(i => (!f.genre.size || i.genres.some(g => f.genre.has(g))) && (!f.decade.size || f.decade.has(`${Math.floor(i.year / 10) * 10}`)) && (!f.cert.size || f.cert.has(i.cert)));
  const key = { added: i => -hashStr(i.id) % 997, name: i => i.title, rating: i => -i.rating, year: i => -i.year, runtime: i => -(i.runtime || 0) }[S.sort];
  return list.sort((a, b) => { const x = key(a), y = key(b); return typeof x === 'string' ? x.localeCompare(y, 'zh-CN') : x - y; });
}
function pageLibrary() {
  const lib = LIBS.find(l => l.id === S.route.params.lib) || LIBS[0];
  const fc = filterCount();
  const sortLabel = SORTS.find(s => s[0] === S.sort)[1];
  const actions = `
    <button class="btn ${S.filtersOpen ? 'btn-accent' : ''}" data-act="toggle-filters">${icon('filter')}筛选${fc ? `<span class="count-dot">${fc}</span>` : ''}</button>
    <div class="menu-anchor"><button class="btn" data-act="toggle-sort" style="min-width:132px;justify-content:space-between">${icon('sort')}<span>${sortLabel}</span>${icon('chevD', 'ico-14')}</button>
      ${S.sortOpen ? `<div class="menu">${SORTS.filter(s => s[0] !== 'runtime' || lib.id === 'movies').map(([k, label]) => `<button class="menu-item" data-act="set-sort" data-sort="${k}"><span class="menu-check">${k === S.sort ? icon('check') : ''}</span>${label}</button>`).join('')}</div>` : ''}</div>`;
  const items = S.libVariant === 'empty' ? [] : libraryItems(lib);
  const pool = lib.items();
  const chipGroup = (group, values, fmt = v => v) => `<button class="chip ${S.filters[group].size ? '' : 'on'}" data-act="filter-all" data-group="${group}">全部</button>` +
    values.map(v => `<button class="chip ${S.filters[group].has(v) ? 'on' : ''}" data-act="filter" data-group="${group}" data-v="${esc(v)}">${esc(fmt(v))}</button>`).join('');
  const genres = [...new Set(pool.flatMap(i => i.genres))];
  const decades = [...new Set(pool.map(i => `${Math.floor(i.year / 10) * 10}`))].sort().reverse();
  const certs = [...new Set(pool.map(i => i.cert))];
  const panel = S.filtersOpen ? `<div class="filter-panel">
      <div class="filter-row"><span class="filter-label">类型</span><div class="chips">${chipGroup('genre', genres)}</div></div>
      <div class="filter-row"><span class="filter-label">年份</span><div class="chips">${chipGroup('decade', decades, d => `${d} 年代`)}</div></div>
      <div class="filter-row"><span class="filter-label">分级</span><div class="chips">${chipGroup('cert', certs)}</div></div>
      ${fc ? `<div class="filter-foot"><button class="link-btn" data-act="filter-reset">重置筛选</button></div>` : ''}</div>` : '';
  const count = S.libVariant === 'empty' ? '0 项' : fc ? `${items.length} 项` : `${lib.count.toLocaleString('zh-CN')} 项`;
  const grid = items.length ? `<div class="grid grid-poster">${items.map(i => posterCard(i)).join('')}</div>
      ${lib.id === 'movies' && !fc ? `<div class="load-more"><span class="p-spinner" style="width:14px;height:14px;border-width:2px;border-color:var(--skeleton);border-top-color:var(--text-2)"></span></div>` : ''}`
    : `<div class="state">${stateIcon('filter')}<h3 class="state-title">当前筛选没有内容</h3><div class="state-actions"><button class="btn btn-accent" data-act="filter-reset">重置筛选</button></div></div>`;
  return `<div class="page">${pageHead('LIBRARY', lib.name, count, actions)}${panel}${grid}</div>`;
}
function pageSearch() {
  const q = S.searchText.trim();
  if (!q) return `<div class="page">${pageHead('SEARCH', '搜索')}<div class="state">${stateIcon('search')}<h3 class="state-title">输入片名开始搜索</h3></div></div>`;
  const groups = LIBS.map(l => ({ lib: l, items: l.items().filter(i => i.title.includes(q)) })).filter(g => g.items.length);
  const body = groups.length ? groups.map(g => {
    const shown = S.searchMore[g.lib.id] ? g.items : g.items.slice(0, 4);
    return `<section class="search-group"><div class="group-head"><h2 class="group-title">${g.lib.name}</h2><span class="group-count">${g.items.length} 项</span></div>
      <div class="grid grid-landscape">${shown.map(i => landscapeCard(i, { mark: q })).join('')}</div>
      ${g.items.length > shown.length ? `<div class="more-row"><button class="btn" data-act="search-more" data-lib="${g.lib.id}">加载更多</button></div>` : ''}</section>`;
  }).join('') : `<div class="state">${stateIcon('search')}<h3 class="state-title">没有找到“${esc(q)}”</h3></div>`;
  return `<div class="page">${pageHead('SEARCH', '搜索')}<p class="page-sub" style="margin-top:-12px;margin-bottom:22px">“${esc(q)}”的搜索结果</p>${body}</div>`;
}
S.searchMore = {};

/* ================= 页面：详情 ================= */
function playCircle(progress) {
  const C = 2 * Math.PI * 33;
  return `<svg class="ring" viewBox="0 0 70 70"><circle cx="35" cy="35" r="33" fill="none" stroke="rgba(255,255,255,.25)" stroke-width="2.5"/>
    ${progress ? `<circle cx="35" cy="35" r="33" fill="none" stroke="#fff" stroke-width="2.5" stroke-linecap="round" stroke-dasharray="${f1(C * progress)} ${f1(C)}"/>` : ''}</svg>`;
}
function peopleRail() {
  return `<section class="section"><div class="section-head"><h2 class="section-title">演职人员</h2></div>
    <div class="rail-track">${PEOPLE.map(p => `<div class="person"><div class="person-photo" style='background-image:${art(p.id, 'square', 'aurora')}'><span class="initial">${esc(p.name[0])}</span></div>
      <div class="person-name">${esc(p.name)}</div><div class="person-role">${esc(p.role)}</div></div>`).join('')}</div></section>`;
}
function pageDetail() {
  const item = ITEMS.get(S.route.params.id);
  const series = item.kind === 'series' ? item : item.kind === 'episode' ? ITEMS.get(item.seriesId) : null;
  if (series) {
    const up = S.selectedEp[series.id] ? ITEMS.get(S.selectedEp[series.id]) : item.kind === 'episode' ? item : nextUp(series);
    const season = S.season[series.id] || up.season;
    const eps = EPISODES.get(`${series.id}:${season}`);
    const prog = progressOf(up);
    return `<div class="detail"><section class="hero detail-hero">
        <div class="hero-art" style='background-image:${art(series.id, 'wide', series.motif)}'></div><div class="hero-scrim"></div>
        <div class="hero-content">${logoHtml(series)}${metaRow(series)}
          <div class="detail-ep-line">第 ${up.season} 季 · 第 ${up.number} 集 · ${esc(up.title)}</div>
          <p class="hero-overview">${esc(up.overview)}</p>
          <div class="detail-actions anno-host" data-d="D8"><button class="play-circle" data-act="play" data-id="${up.id}" aria-label="播放">${playCircle(prog)}${icon('play')}</button>
            <div class="play-label"><b>${prog ? '继续播放' : up.played ? '重新播放' : '播放'} 第 ${up.number} 集</b><span>${prog ? `剩余 ${fmtMinutes(Math.round(up.runtime * (1 - prog)))}` : `${up.runtime} 分钟`}</span></div></div>
        </div></section>
      <section class="section"><div class="section-head"><h2 class="section-title">剧集</h2>
          <div class="season-tabs">${Array.from({ length: series.seasons }, (_, k) => `<button class="season-tab ${k + 1 === season ? 'on' : ''}" data-act="season" data-sid="${series.id}" data-season="${k + 1}">第 ${k + 1} 季</button>`).join('')}</div></div>
        <button class="rail-arrow left" data-act="rail-scroll" data-dir="-1" style="top:62%">${icon('chevL')}</button>
        <div class="rail-track">${eps.map(e => episodeCard(e, e.id === up.id)).join('')}</div>
        <button class="rail-arrow right" data-act="rail-scroll" data-dir="1" style="top:62%">${icon('chevR')}</button></section>
      ${peopleRail()}<div style="height:48px"></div></div>`;
  }
  const prog = progressOf(item);
  return `<div class="detail"><section class="hero detail-hero">
      <div class="hero-art" style='background-image:${art(item.id, 'wide', motifOf(item))}'></div><div class="hero-scrim"></div>
      <div class="hero-content">${logoHtml(item)}${metaRow(item)}<p class="hero-overview">${esc(item.overview)}</p>
        <div class="detail-actions anno-host" data-d="D8"><button class="play-circle" data-act="play" data-id="${item.id}" aria-label="播放">${playCircle(prog)}${icon('play')}</button>
          <div class="play-label"><b>${prog ? '继续播放' : item.played ? '重新播放' : '播放'}</b><span>${prog ? `剩余 ${fmtMinutes(Math.round(item.runtime * (1 - prog)))}` : fmtMinutes(item.runtime)}</span></div></div>
      </div></section>${peopleRail()}<div style="height:48px"></div></div>`;
}

/* ================= 页面：设置 ================= */
function pageSettings() {
  const server = S.loggedIn
    ? `<div class="set-card"><div class="avatar">演</div><div class="set-text"><div class="set-title">演示用户</div><div class="set-desc">demo.example</div></div>
        <div class="set-control"><button class="btn btn-danger-text" data-act="logout">${icon('logout', 'ico-14')}断开连接</button></div></div>`
    : `<div class="set-card col"><div class="set-row">${icon('server', 'set-icon')}<div class="set-text"><div class="set-title">连接 Emby 服务器</div></div></div>
        <div class="form-grid"><label class="field"><span class="field-label">服务器地址</span><input class="input" placeholder="https://your-emby-server" value="https://demo.example"></label>
          <label class="field"><span class="field-label">用户名</span><input class="input" placeholder="用户名" value="演示用户"></label>
          <label class="field"><span class="field-label">密码</span><input class="input" type="password" placeholder="可以为空"></label>
          <button class="btn btn-accent" data-act="login" ${S.connecting ? 'disabled' : ''} style="min-width:96px">${S.connecting ? '连接中…' : '连接'}</button></div></div>`;
  const mpvStatus = { ok: ['ok', 'success', '已验证'], bad: ['warn', 'warning', '未找到 mpv.exe'], checking: ['', 'info', '正在验证…'] }[S.mpvStatus];
  const hdrLabel = { auto: '自动', always: '始终 HDR', off: '关闭' }[S.hdr];
  const seg = (act, key, value, label, disabled = false) => `<button class="seg ${value === key ? 'on' : ''}" data-act="${act}" data-mode="${key}" ${disabled ? 'disabled' : ''}>${label}</button>`;
  const row = (ico, title, control, sub = '') => `<div class="set-card">${icon(ico, 'set-icon')}<div class="set-text"><div class="set-title">${title}</div>${sub}</div><div class="set-control">${control}</div></div>`;
  return `<div class="page"><div class="set-wrap">${pageHead('SETTINGS', '设置')}
    <div class="set-group"><h2 class="set-group-title">服务器</h2>${server}</div>
    <div class="set-group"><h2 class="set-group-title">播放</h2>
      ${row('display', '播放方式', `<div class="segmented">${seg('mode', 'embedded', S.playbackMode, '内置播放器')}${seg('mode', 'external', S.playbackMode, '外部窗口', S.mpvStatus !== 'ok')}</div>`)}
      ${row('file', '外部 mpv 路径', `<div class="path-row"><input class="input" id="mpv-path" placeholder="C:\\Program Files\\mpv\\mpv.exe" value="${esc(S.mpvPath)}"><button class="btn" data-act="pick-mpv">选择文件</button></div>`,
        mpvStatus ? `<div class="status-line ${mpvStatus[0]}">${icon(mpvStatus[1], 'ico-14')}${mpvStatus[2]}</div>` : '')}
      ${row('sun', 'HDR', `<div class="menu-anchor"><button class="select" data-act="hdr-menu"><span>${hdrLabel}</span>${icon('chevD', 'ico-14')}</button>
          ${S.hdrMenu ? `<div class="menu">${[['auto', '自动'], ['always', '始终 HDR'], ['off', '关闭']].map(([k, l]) => `<button class="menu-item" data-act="set-hdr" data-v="${k}"><span class="menu-check">${k === S.hdr ? icon('check') : ''}</span>${l}</button>`).join('')}</div>` : ''}</div>`)}
      ${row('chip', '硬件解码', `<button class="toggle" data-act="hwdec"><span>${S.hwdec ? '开' : '关'}</span><span class="switch ${S.hwdec ? 'on' : ''}"></span></button>`)}
      ${row('play', '预览播放页', `<button class="btn" data-act="preview-player">打开</button>`)}</div>
    <div class="set-group"><h2 class="set-group-title">外观</h2>
      ${row('theme', '主题', `<div class="segmented">${seg('theme-mode', 'system', S.themeMode, '跟随系统')}${seg('theme-mode', 'light', S.themeMode, '浅色')}${seg('theme-mode', 'dark', S.themeMode, '深色')}</div>`)}</div>
    <div class="set-group"><h2 class="set-group-title">关于</h2>
      ${row('info', 'Mambo <span class="version-badge">v0.3.0</span>', `<button class="btn">${icon('book', 'ico-14')}第三方许可</button>`)}
      ${row('trash', '缓存 <span class="set-value">312 MB</span>', `<button class="btn">${icon('folder', 'ico-14')}打开日志目录</button><button class="btn" data-act="clear-cache">清除缓存</button>`)}</div>
  </div></div>`;
}

/* ================= 渲染总入口 ================= */
function renderPage(opts = {}) {
  const r = S.route.name;
  const html ={ home: pageHome, recent: pageRecent, library: pageLibrary, detail: pageDetail, search: pageSearch, settings: pageSettings }[r]();
  const el = host();
  el.innerHTML = `<div class="${opts.enter && !effectiveReduced() ? 'page-enter' : ''}" style="min-height:100%">${html}</div>`;
  if (opts.scroll != null) el.scrollTop = opts.scroll;
}
function render(opts = {}) {
  const w = win();
  w.dataset.theme = S.theme; w.dataset.size = S.size;
  w.classList.toggle('reduce-motion', effectiveReduced());
  w.classList.toggle('annotate', S.annotate);
  w.classList.toggle('playing', !!S.player);
  w.classList.toggle('fullscreen', !!S.player?.fullscreen);
  document.body.classList.toggle('desk-dark', S.theme === 'dark');
  renderTitlebar(); renderSidebar();
  if (opts.page !== false) renderPage(opts);
  renderPlayer(); renderDialog(); renderToasts(); renderReview();
  fit();
}

/* ================= 播放层 ================= */
let playerGeneration = 0;
let pendingPlayerClose = null;
let surfaceClickTimer = null;
function resetPlayerRequest() {
  ++playerGeneration;
  clearTimeout(openPlayer.timer);
  clearTimeout(surfaceClickTimer);
  if (pendingPlayerClose) {
    pendingPlayerClose.resolve(false);
    pendingPlayerClose = null;
  }
  return playerGeneration;
}
function queuePlayerReady(p, delay) {
  clearTimeout(openPlayer.timer);
  const generation = playerGeneration;
  openPlayer.timer = setTimeout(() => {
    if (generation !== playerGeneration || S.player !== p || S._closing || p.phase !== 'opening' || p.pinned) return;
    p.phase = 'playing';
    renderPlayer(); renderTitlebar();
  }, delay);
}
function buildEntries(id) {
  const item = ITEMS.get(id);
  const toEntry = e => ({ id: e.id, title: e.title, seriesTitle: e.seriesTitle, season: e.season, number: e.number, runtime: e.runtime * 60, played: e.played, progress: progressOf(e), motif: e.motif });
  if (item.kind === 'series') { const up = nextUp(item); return { entries: seasonPlan(up).map(toEntry), index: up.number - 1 }; }
  if (item.kind === 'episode') return { entries: seasonPlan(item).map(toEntry), index: item.number - 1 };
  return { entries: [{ id: item.id, title: item.title, runtime: item.runtime * 60, progress: progressOf(item), motif: motifOf(item) }], index: 0 };
}
function requestPlay(id) {
  const current = S.player?.entries[S.player.index];
  if (S.player && !S._closing && current && current.id !== id) {
    return openDialog({ title: '切换播放？', text: '当前播放将结束并保存进度。', confirm: '切换', danger: true, onConfirm: () => openPlayer(id) });
  }
  openPlayer(id);
}
function openPlayer(id, state = {}) {
  const { entries, index } = buildEntries(id);
  const e = entries[index];
  const generation = resetPlayerRequest();
  S.player = { entries, index, phase: 'opening', slow: false, pos: e.progress * e.runtime, paused: false, buffering: false, rate: 1, volume: 80, muted: false,
    chrome: true, menu: null, drawer: false, drawerStyle: S.player?.drawerStyle || 'list', upNext: false, fullscreen: false, sub: 's1', audio: 'a1', pinned: false, lastMove: Date.now(), ...state };
  const p = S.player;
  S.dialog = null; S._closing = false; S._entering = !effectiveReduced();
  render({ page: false });
  if (S._entering) requestAnimationFrame(() => requestAnimationFrame(() => {
    if (generation !== playerGeneration || S.player !== p || S._closing || !S._entering) return;
    S._entering = false;
    $('#player').classList.add('open');
  }));
  if (!p.pinned) queuePlayerReady(p, 1400);
}
function requestClosePlayer() { closePlayer(); }
function closePlayer() {
  const generation = ++playerGeneration;
  clearTimeout(openPlayer.timer);
  clearTimeout(surfaceClickTimer);
  if (!S.player) return Promise.resolve(true);
  if (pendingPlayerClose) {
    pendingPlayerClose.generation = generation;
    return pendingPlayerClose.promise;
  }
  let resolve;
  const promise = new Promise(done => { resolve = done; });
  const closing = { player: S.player, generation, promise, resolve };
  pendingPlayerClose = closing;
  S._closing = true; S._entering = false;
  const el = $('#player');
  el.inert = true;
  el.classList.remove('open');
  S.player.fullscreen = false; win().classList.remove('fullscreen');
  const animations = el.getAnimations().filter(a => Number.isFinite(a.effect.getComputedTiming().endTime));
  if (effectiveReduced() || !animations.length) finishPlayerClose(closing);
  else Promise.allSettled(animations.map(a => a.finished)).then(() => finishPlayerClose(closing));
  return promise;
}
function finishPlayerClose(closing) {
  if (pendingPlayerClose !== closing || closing.generation !== playerGeneration || S.player !== closing.player) return;
  pendingPlayerClose = null;
  S.player = null; S._closing = false; S._entering = false;
  render({ page: false });
  closing.resolve(true);
}
const SPEEDS = [0.5, 0.75, 1, 1.25, 1.5, 2];
const SUBS = [{ id: 's1', label: '简体中文', lang: '中文' }, { id: 's2', label: '繁體中文', lang: '中文' }, { id: 's3', label: 'English', lang: '英语' }];
const AUDIOS = [{ id: 'a1', label: '原声 · 5.1', lang: '中文' }, { id: 'a2', label: '导演评论', lang: '中文' }, { id: 'a3', label: 'English · 立体声', lang: '英语' }];
function renderPlayer() {
  const el = $('#player'), p = S.player;
  el.inert = !!p && !!S._closing;
  if (!p) { el.className = 'player'; el.innerHTML = ''; return; }
  const e = p.entries[p.index], canPrev = p.index > 0, canNext = p.index < p.entries.length - 1, hasEps = p.entries.length > 1;
  let center = '';
  if (p.phase === 'opening') center = `<div class="p-stack"><div class="p-spinner"></div><div class="p-status">正在打开</div>${p.slow ? `<div class="p-hint">片源响应较慢</div><button class="btn btn-glass" data-act="close-player">关闭播放</button>` : ''}</div>`;
  else if (p.phase === 'failed') center = `<div class="p-error"><div class="err-icon">${icon('error', 'ico-24')}</div><h3>这部片子暂时打不开</h3><p>服务器返回 503</p>
      <div class="p-error-actions"><button class="btn btn-white" data-act="p-retry">${icon('refresh')}从断点重试</button><button class="btn btn-glass" data-act="close-player">关闭</button></div></div>`;
  else if (p.buffering) center = `<div class="p-pill"><span class="p-spinner"></span>缓冲中…</div>`;
  else if (p.paused && p.chrome) center = `<button class="p-bigplay" data-act="p-toggle" aria-label="播放">${icon('play')}</button>`;
  const menu = p.menu === 'speed'
    ? `<div class="p-menu" style="width:180px"><div class="p-menu-title">播放速度</div>${SPEEDS.map(s => `<button class="p-menu-item" data-act="p-speed" data-v="${s}"><span class="check">${s === p.rate ? icon('check') : ''}</span>${s === 1 ? '正常' : s + '×'}</button>`).join('')}</div>`
    : p.menu === 'tracks'
      ? `<div class="p-menu"><div class="p-menu-title">字幕</div><button class="p-menu-item" data-act="p-sub" data-v=""><span class="check">${p.sub ? '' : icon('check')}</span>关闭字幕</button>
          ${SUBS.map(s => `<button class="p-menu-item" data-act="p-sub" data-v="${s.id}"><span class="check">${p.sub === s.id ? icon('check') : ''}</span>${s.label}<span class="lang">${s.lang}</span></button>`).join('')}
          <div class="p-menu-sep"></div><div class="p-menu-title">音轨</div>
          ${AUDIOS.map(a => `<button class="p-menu-item" data-act="p-audio" data-v="${a.id}"><span class="check">${p.audio === a.id ? icon('check') : ''}</span>${a.label}<span class="lang">${a.lang}</span></button>`).join('')}</div>`
      : '';
  const next = canNext ? p.entries[p.index + 1] : null;
  const upnext = p.upNext && next && !p.drawer && p.phase === 'playing' ? `<div class="p-upnext anno-host" data-d="D5"><div class="p-upnext-thumb" style='background-image:${art(next.id, 'wide', next.motif)}'></div>
      <div class="p-upnext-body"><div class="p-upnext-label">即将播放 · <span class="t-left">0:15</span></div><div class="p-upnext-title">第 ${next.number} 集 · ${esc(next.title)}</div>
        <div class="p-upnext-actions"><button class="btn btn-white btn-sm" data-act="p-next">${icon('play', 'ico-14')}立即播放</button><button class="btn btn-glass btn-sm" data-act="p-upnext-dismiss">取消</button></div></div></div>` : '';
  const drawerBody = p.drawerStyle === 'grid'
    ? `<div class="p-ep-grid">${p.entries.map((x, i) => `<button class="p-ep-num ${i === p.index ? 'current' : ''} ${x.played ? 'watched' : ''}" data-act="p-pick" data-i="${i}" title="${esc(x.title)}">${x.number}</button>`).join('')}</div>`
    : `<div class="p-ep-list">${p.entries.map((x, i) => `<button class="p-ep ${i === p.index ? 'current' : ''}" data-act="p-pick" data-i="${i}">
        <div class="p-ep-thumb" style='background-image:${art(x.id, 'wide', x.motif)}'>${i === p.index ? `<div class="now">${icon('play')}</div>` : x.progress ? `<div class="progress"><i style="width:${x.progress * 100}%"></i></div>` : ''}</div>
        <div><div class="p-ep-title">${x.number}. ${esc(x.title)}</div><div class="p-ep-sub">${Math.round(x.runtime / 60)} 分钟${x.played ? `${icon('check')}已看完` : i === p.index ? ' · 正在播放' : ''}</div></div></button>`).join('')}</div>`;
  el.innerHTML = `
    <div class="video ${p.phase === 'opening' ? 'black' : ''}" data-act="p-surface" style='background-image:${art(e.id, 'wide', e.motif)}'></div>
    <div class="p-chrome">
      <div class="p-top"><div class="p-titles"><div class="p-title">${esc(e.seriesTitle || e.title)}</div><div class="p-sub">${e.seriesTitle ? `第 ${e.season} 季 · 第 ${e.number} 集 · ${esc(e.title)}` : '电影'}</div></div>
        <div class="p-badges"><span class="p-badge">4K</span><span class="p-badge">HDR10</span></div></div>
      <div class="p-bottom">
        <div class="scrub" data-act="p-seek"><div class="scrub-track"><div class="scrub-buffer"></div><div class="scrub-fill"></div><div class="scrub-thumb"></div></div><div class="scrub-tip num">0:00</div></div>
        <div class="p-row">
          ${hasEps ? `<button class="p-btn" data-act="p-prev" ${canPrev ? '' : 'disabled'} title="上一集">${icon('prev')}</button>` : ''}
          <button class="p-play" data-act="p-toggle" title="播放/暂停（空格）">${icon(p.paused || p.phase !== 'playing' ? 'play' : 'pause')}</button>
          ${hasEps ? `<button class="p-btn" data-act="p-next" ${canNext ? '' : 'disabled'} title="下一集">${icon('next')}</button>` : ''}
          <span class="p-time"><b class="t-pos">0:00</b> / <span class="t-dur">0:00</span></span>
          <span class="p-spacer"></span>
          <button class="p-btn ${p.menu === 'speed' ? 'on' : ''}" data-act="p-menu" data-menu="speed" title="倍速（[ 和 ]）">${p.rate === 1 ? '倍速' : p.rate + '×'}</button>
          <button class="p-btn ${p.menu === 'tracks' ? 'on' : ''}" data-act="p-menu" data-menu="tracks" title="字幕与音轨（C / V）">${icon('cc')}</button>
          <div class="vol"><button class="p-btn" data-act="p-mute" title="静音（M）">${icon(p.muted ? 'mute' : 'volume')}</button><div class="vol-slider"><i style="width:${p.muted ? 0 : p.volume}%"></i></div></div>
          ${hasEps ? `<button class="p-btn anno-host ${p.drawer ? 'on' : ''}" data-act="p-drawer" data-d="D5" title="选集">${icon('list')}<span>选集</span></button>` : ''}
          <button class="p-btn" data-act="p-fullscreen" title="全屏（F11）">${icon(p.fullscreen ? 'unfullscreen' : 'fullscreen')}</button>
        </div>
      </div>
      ${menu}
    </div>
    <div class="p-center">${center}</div>
    ${upnext}
    <aside class="p-drawer ${p.drawer ? 'open' : ''}"><div class="p-drawer-head"><h3>选集</h3>
        <button class="p-btn ${p.drawerStyle === 'list' ? 'on' : ''}" data-act="p-drawer-style" data-v="list" title="列表">${icon('list')}</button>
        <button class="p-btn ${p.drawerStyle === 'grid' ? 'on' : ''}" data-act="p-drawer-style" data-v="grid" title="集号">${icon('grid')}</button>
        <button class="p-btn" data-act="p-drawer" title="关闭">${icon('close')}</button></div>
      ${e.seriesTitle ? `<div class="p-tabs"><button class="p-tab on">第 ${e.season} 季</button></div>` : ''}${drawerBody}</aside>`;
  el.className = `player${S._entering || S._closing ? '' : ' open'}${p.phase === 'failed' ? ' failed' : ''}`;
  el.classList.toggle('chrome-hidden', !p.chrome && p.phase === 'playing');
  updatePlayerTime();
}
function updatePlayerTime() {
  const p = S.player; if (!p) return;
  const e = p.entries[p.index], dur = e.runtime, pos = Math.min(p.pos, dur);
  const pct = dur ? pos / dur * 100 : 0, buf = Math.min(100, pct + 9);
  const el = $('#player');
  const q = s => el.querySelector(s);
  if (!q('.scrub')) return;
  q('.scrub-fill').style.width = pct + '%';
  q('.scrub-buffer').style.width = (p.phase === 'opening' ? 0 : buf) + '%';
  q('.scrub-thumb').style.left = pct + '%';
  q('.t-pos').textContent = fmtTime(pos);
  q('.t-dur').textContent = fmtTime(dur);
  const left = q('.t-left'); if (left) left.textContent = fmtTime(dur - pos);
}
function setChrome(visible) {
  const p = S.player; if (!p || S._closing) return;
  if (p.chrome === visible) return;
  p.chrome = visible;
  const el = $('#player');
  el.classList.toggle('chrome-hidden', !visible && p.phase === 'playing');
  if (p.paused) renderPlayer();
}
setInterval(() => {
  const p = S.player; if (!p || p.pinned || S._closing) return;
  const e = p.entries[p.index];
  if (p.phase === 'playing' && !p.paused && !p.buffering) {
    p.pos += .25 * p.rate;
    const left = e.runtime - p.pos;
    const canNext = p.index < p.entries.length - 1;
    if (canNext && left <= 20 && !p.upNext && !p.upNextDismissed) { p.upNext = true; renderPlayer(); }
    if (left <= 0) { if (canNext) switchEntry(p.index + 1); else { closePlayer(); toast('success', '本季已播放完毕。'); } return; }
    updatePlayerTime();
  }
  if (p.chrome && p.phase === 'playing' && !p.paused && !p.menu && !p.drawer && Date.now() - p.lastMove > 3000) setChrome(false);
}, 250);
function switchEntry(i) {
  const p = S.player; if (!p || S._closing) return;
  p.index = i; p.pos = 0; p.phase = 'opening'; p.paused = false; p.buffering = false; p.upNext = false; p.upNextDismissed = false; p.menu = null;
  renderPlayer(); renderTitlebar();
  queuePlayerReady(p, 900);
}
function playerAct(act, el) {
  const p = S.player; if (!p || S._closing) return;
  p.pinned = false; p.lastMove = Date.now();
  switch (act) {
    case 'p-surface':
      clearTimeout(surfaceClickTimer);
      surfaceClickTimer = setTimeout(() => { if (S.player !== p || S._closing) return; if (p.menu) { p.menu = null; renderPlayer(); return; } if (p.phase === 'playing') { p.paused = !p.paused; renderPlayer(); } }, 250);
      return;
    case 'p-toggle': if (p.phase === 'playing') { p.paused = !p.paused; renderPlayer(); } return;
    case 'p-prev': if (p.index > 0) switchEntry(p.index - 1); return;
    case 'p-next': if (p.index < p.entries.length - 1) switchEntry(p.index + 1); return;
    case 'p-pick': { const i = +el.dataset.i; if (i !== p.index) switchEntry(i); return; }
    case 'p-menu': p.menu = p.menu === el.dataset.menu ? null : el.dataset.menu; renderPlayer(); return;
    case 'p-speed': p.rate = +el.dataset.v; p.menu = null; renderPlayer(); return;
    case 'p-sub': p.sub = el.dataset.v; renderPlayer(); return;
    case 'p-audio': p.audio = el.dataset.v; renderPlayer(); return;
    case 'p-mute': p.muted = !p.muted; renderPlayer(); return;
    case 'p-drawer': p.drawer = !p.drawer; p.menu = null; renderPlayer(); return;
    case 'p-drawer-style': p.drawerStyle = el.dataset.v; renderPlayer(); return;
    case 'p-fullscreen': p.fullscreen = !p.fullscreen; render({ page: false }); return;
    case 'p-retry': p.phase = 'opening'; renderPlayer(); queuePlayerReady(p, 1000); return;
    case 'p-upnext-dismiss': p.upNext = false; p.upNextDismissed = true; renderPlayer(); return;
    case 'p-seek': {
      const track = el.querySelector('.scrub-track').getBoundingClientRect();
      const ratio = Math.min(1, Math.max(0, (lastPointer.x - track.left) / track.width));
      p.pos = ratio * p.entries[p.index].runtime; p.upNext = false; updatePlayerTime(); return;
    }
  }
}
let lastPointer = { x: 0, y: 0 };

/* ================= 对话框与通知 ================= */
function openDialog(d) { S.dialog = d; renderDialog(); }
function closeDialog() { S.dialog = null; renderDialog(); }
function renderDialog() {
  const el = $('#dialog'), d = S.dialog;
  el.classList.toggle('open', !!d);
  el.innerHTML = d ? `<div class="dialog" role="dialog" aria-modal="true"><h2 class="dialog-title">${esc(d.title)}</h2><p class="dialog-text">${esc(d.text)}</p>
    <div class="dialog-foot"><button class="btn" data-act="dialog-cancel">取消</button><button class="btn ${d.danger ? 'btn-danger-text' : 'btn-accent'}" data-act="dialog-ok">${esc(d.confirm || '确认')}</button></div></div>` : '';
}
let toastSeq = 0;
function toast(kind, text, action) {
  const t = { id: ++toastSeq, kind, text, action };
  S.toasts.push(t);
  while (S.toasts.length > 3) { const i = S.toasts.findIndex(x => x.kind !== 'error'); S.toasts.splice(i < 0 ? 0 : i, 1); }
  renderToasts();
  if (kind !== 'error') setTimeout(() => { S.toasts = S.toasts.filter(x => x.id !== t.id); renderToasts(); }, kind === 'warning' ? 6000 : 3500);
}
function renderToasts() {
  $('#toasts').innerHTML = S.toasts.map(t => `<div class="toast ${t.kind}" role="status">${icon({ info: 'info', success: 'success', warning: 'warning', error: 'error' }[t.kind])}
    <div class="toast-body"><div class="toast-text">${esc(t.text)}</div>${t.action ? `<div class="toast-actions"><button class="btn btn-sm" data-act="toast-dismiss" data-id="${t.id}">${esc(t.action)}</button></div>` : ''}</div>
    <button class="toast-close" data-act="toast-dismiss" data-id="${t.id}" aria-label="关闭">${icon('close', 'ico-14')}</button></div>`).join('');
}

/* ================= 事件 ================= */
const ACT = {
  noop() {},
  back: goBack, forward: goForward,
  'go-home'() { nav('home'); }, 'go-recent'() { nav('recent'); }, 'go-settings'() { nav('settings'); },
  'go-lib'(el) { S.filtersOpen = false; nav('library', { lib: el.dataset.lib }); },
  detail(el) { nav('detail', { id: el.dataset.id }); },
  play(el) { requestPlay(el.dataset.id); },
  'select-ep'(el) { const ep = ITEMS.get(el.dataset.id); S.selectedEp[ep.seriesId] = ep.id; rerenderKeepingScroll(); },
  season(el) { S.season[el.dataset.sid] = +el.dataset.season; rerenderKeepingScroll(0); },
  'rail-scroll'(el) { const track = el.parentElement.querySelector('.rail-track'); track.scrollBy({ left: +el.dataset.dir * track.clientWidth * .82, behavior: effectiveReduced() ? 'auto' : 'smooth' }); },
  'hero-go'(el) { setHero(+el.dataset.i); }, 'hero-prev'() { setHero(S.heroIndex - 1); }, 'hero-next'() { setHero(S.heroIndex + 1); },
  'toggle-filters'() { S.filtersOpen = !S.filtersOpen; S.sortOpen = false; renderPage(); },
  'toggle-sort'() { S.sortOpen = !S.sortOpen; renderPage(); },
  'set-sort'(el) { S.sort = el.dataset.sort; S.sortOpen = false; renderPage({ scroll: 0 }); },
  filter(el) { const set = S.filters[el.dataset.group]; set.has(el.dataset.v) ? set.delete(el.dataset.v) : set.add(el.dataset.v); S.libVariant = 'normal'; renderPage(); },
  'filter-all'(el) { S.filters[el.dataset.group].clear(); renderPage(); },
  'filter-reset'() { Object.values(S.filters).forEach(s => s.clear()); S.libVariant = 'normal'; renderPage(); },
  'search-clear'() { S.searchText = ''; renderSidebar(); if (S.route.name === 'search') renderPage(); $('#search-input')?.focus(); },
  'search-more'(el) { S.searchMore[el.dataset.lib] = true; renderPage(); },
  'retry-home'() { S.homeVariant = 'loading'; renderPage(); setTimeout(() => { S.homeVariant = 'normal'; render({ enter: true }); }, 1200); },
  login() { S.connecting = true; renderPage(); setTimeout(() => { S.connecting = false; S.loggedIn = true; toast('success', '已连接到 demo.example。'); nav('home'); }, 1100); },
  logout() {
    const doLogout = () => { closePlayer(); S.loggedIn = false; S.back = []; S.fwd = []; toast('info', '已断开连接。'); render({ enter: true }); };
    if (S.player) openDialog({ title: '注销并结束播放？', text: '当前播放将结束并保存进度。', confirm: '注销', danger: true, onConfirm: doLogout });
    else doLogout();
  },
  mode(el) { S.playbackMode = el.dataset.mode; renderPage(); },
  'theme-mode'(el) { S.themeMode = el.dataset.mode; S.theme = resolveTheme(); render({ scroll: host().scrollTop }); },
  'pick-mpv'() { S.mpvPath = 'C:\\Program Files\\mpv\\mpv.exe'; validateMpv(); },
  'hdr-menu'() { S.hdrMenu = !S.hdrMenu; renderPage(); },
  'set-hdr'(el) { S.hdr = el.dataset.v; S.hdrMenu = false; renderPage(); },
  hwdec() { S.hwdec = !S.hwdec; renderPage(); },
  'preview-player'() { requestPlay('s1e1x3'); },
  'clear-cache'() { openDialog({ title: '清除缓存？', text: '登录信息和设置不受影响。', confirm: '清除', onConfirm: () => toast('success', '已清除 312 MB 缓存。') }); },
  'dialog-ok'() { const d = S.dialog; closeDialog(); d?.onConfirm?.(); },
  'dialog-cancel'() { closeDialog(); },
  'toast-dismiss'(el) { S.toasts = S.toasts.filter(t => t.id !== +el.dataset.id); renderToasts(); },
  'close-player'() { closePlayer(); },
  'win-close'() {
    if (S.player) openDialog({ title: '退出应用？', text: '当前播放将结束并保存进度。', confirm: '退出', danger: true, onConfirm: () => closePlayer() });
  },
  snap() { S.snapOpen = !S.snapOpen; renderTitlebar(); },
};
// 局部刷新时保留页面与横向列表的滚动位置；resetRail 指定需要回到开头的列表序号。
function rerenderKeepingScroll(resetRail = -1) {
  const tracks = [...host().querySelectorAll('.rail-track')].map(t => t.scrollLeft);
  const top = host().scrollTop;
  renderPage();
  host().scrollTop = top;
  host().querySelectorAll('.rail-track').forEach((t, i) => {
    t.style.scrollBehavior = 'auto';
    t.scrollLeft = i === resetRail ? 0 : tracks[i] || 0;
    t.style.scrollBehavior = '';
  });
}
function setHero(i) {
  const next = (i + HERO.length) % HERO.length;
  if (next === S.heroIndex) return;
  S.heroIndex = next;
  const old = host().querySelector('.home > .hero');
  const pageEntering = host().firstElementChild?.getAnimations().some(a => a.playState === 'running');
  if (old) old.outerHTML = heroHtml(HERO[S.heroIndex], !pageEntering);
  renderTitlebar();
}
function validateMpv() {
  S.mpvStatus = S.mpvPath.trim() ? 'checking' : 'none'; renderPage();
  clearTimeout(validateMpv.t);
  validateMpv.t = setTimeout(() => { S.mpvStatus = !S.mpvPath.trim() ? 'none' : /mpv\.exe$/i.test(S.mpvPath.trim()) ? 'ok' : 'bad'; if (S.mpvStatus !== 'ok') S.playbackMode = 'embedded'; renderPage(); }, 500);
}
document.addEventListener('pointermove', e => {
  lastPointer = { x: e.clientX, y: e.clientY };
  if (S.player && !S._closing && e.target.closest('#player')) {
    S.player.lastMove = Date.now();
    if (!S.player.chrome && !e.target.closest('.p-drawer')) setChrome(true);
    const scrub = e.target.closest('.scrub');
    if (scrub) {
      const tr = scrub.querySelector('.scrub-track').getBoundingClientRect(), ratio = Math.min(1, Math.max(0, (e.clientX - tr.left) / tr.width));
      const tip = scrub.querySelector('.scrub-tip'); tip.style.left = ratio * 100 + '%'; tip.textContent = fmtTime(ratio * S.player.entries[S.player.index].runtime);
    }
  }
});
document.addEventListener('pointerdown', e => { lastPointer = { x: e.clientX, y: e.clientY }; });
document.addEventListener('click', e => {
  const el = e.target.closest('[data-act]');
  if (S.snapOpen && !e.target.closest('.snap-flyout, [data-rv="common:snap"]') && el?.dataset.act !== 'snap') { S.snapOpen = false; renderTitlebar(); }
  if (S.sortOpen && !e.target.closest('.menu-anchor')) { S.sortOpen = false; if (S.route.name === 'library') renderPage(); }
  if (S.hdrMenu && !e.target.closest('.menu-anchor')) { S.hdrMenu = false; if (S.route.name === 'settings') renderPage(); }
  if (!el || el.closest('#review')) return;
  if (el.disabled) return;
  const act = el.dataset.act;
  if (act.startsWith('p-')) { e.stopPropagation(); el.blur?.(); playerAct(act, el); return; }
  if (el.closest('.card') && act === 'play') e.stopPropagation();
  ACT[act]?.(el, e);
});
document.addEventListener('dblclick', e => {
  if (e.target.closest('[data-act="p-surface"]') && S.player && !S._closing) { clearTimeout(surfaceClickTimer); S.player.fullscreen = !S.player.fullscreen; render({ page: false }); }
});
document.addEventListener('mouseup', e => {
  if (e.button === 3) { e.preventDefault(); goBack(); }
  if (e.button === 4) { e.preventDefault(); goForward(); }
});
document.addEventListener('keydown', e => {
  const typing = e.target.matches?.('input, textarea');
  if (e.target.id === 'search-input' && e.key === 'Enter') { S.searchText = e.target.value; S.searchMore = {}; nav('search', { q: S.searchText }); renderSidebar(); return; }
  if (e.target.id === 'mpv-path' && e.key === 'Enter') { S.mpvPath = e.target.value; validateMpv(); return; }
  if (S.dialog && e.key === 'Escape') { closeDialog(); return; }
  if (e.altKey && e.key === 'ArrowLeft') { e.preventDefault(); goBack(); return; }
  if (e.altKey && e.key === 'ArrowRight') { e.preventDefault(); goForward(); return; }
  const p = S.player;
  if (p && S._closing) return;
  if (p && !typing) {
    const keys = {
      ' ': () => playerAct('p-toggle'), k: () => playerAct('p-toggle'), f: () => playerAct('p-fullscreen'), F11: () => playerAct('p-fullscreen'),
      m: () => playerAct('p-mute'),
      ArrowLeft: () => { p.pos = Math.max(0, p.pos - 5); updatePlayerTime(); }, ArrowRight: () => { p.pos += 5; updatePlayerTime(); },
      ArrowUp: () => { p.volume = Math.min(100, p.volume + 5); p.muted = false; renderPlayer(); }, ArrowDown: () => { p.volume = Math.max(0, p.volume - 5); renderPlayer(); },
      '[': () => { const i = SPEEDS.indexOf(p.rate); p.rate = SPEEDS[Math.max(0, i - 1)]; renderPlayer(); }, ']': () => { const i = SPEEDS.indexOf(p.rate); p.rate = SPEEDS[Math.min(SPEEDS.length - 1, i + 1)]; renderPlayer(); },
      c: () => { const ids = ['', ...SUBS.map(s => s.id)]; p.sub = ids[(ids.indexOf(p.sub) + 1) % ids.length]; renderPlayer(); toast('info', `字幕：${SUBS.find(s => s.id === p.sub)?.label || '关闭'}`); },
      v: () => { const ids = AUDIOS.map(a => a.id); p.audio = ids[(ids.indexOf(p.audio) + 1) % ids.length]; renderPlayer(); toast('info', `音轨：${AUDIOS.find(a => a.id === p.audio).label}`); },
      Escape: () => { if (p.menu) { p.menu = null; renderPlayer(); } else if (p.drawer) { p.drawer = false; renderPlayer(); } else if (p.fullscreen) playerAct('p-fullscreen'); else closePlayer(); },
    };
    const fn = keys[e.key]; if (fn) { e.preventDefault(); p.pinned = false; p.lastMove = Date.now(); setChrome(true); fn(); }
    return;
  }
  if (!typing && (e.key === '/' || (e.ctrlKey && e.key.toLowerCase() === 'f'))) { e.preventDefault(); $('#search-input')?.focus(); }
  if (!typing && e.key === 'Escape') goBack();
  if (e.ctrlKey && e.key === ',') { e.preventDefault(); nav('settings'); }
});
document.addEventListener('input', e => { if (e.target.id === 'mpv-path') { S.mpvPath = e.target.value; } });
document.addEventListener('mouseover', e => { if (e.target.closest?.('.home > .hero')) S.heroHover = true; });
document.addEventListener('mouseout', e => { if (e.target.closest?.('.home > .hero') && !e.relatedTarget?.closest?.('.home > .hero')) S.heroHover = false; });
let heroTimer = null;
function resetHeroTimer() {
  clearInterval(heroTimer);
  heroTimer = null;
  if (effectiveReduced()) return;
  heroTimer = setInterval(() => {
    if (S.route.name === 'home' && S.loggedIn && S.homeVariant === 'normal' && !S.player && !S.heroHover && !effectiveReduced() && !S.dialog) setHero(S.heroIndex + 1);
  }, 7000);
}

/* ================= 缩放适配 ================= */
function fit() {
  const w = S.size === 'large' ? 1500 : 1100, h = S.size === 'large' ? 860 : 720;
  const desk = $('#desk'), stage = $('#stage');
  const s = S.fit ? Math.min(1, (desk.clientWidth - 48) / w, (desk.clientHeight - 48) / h) : 1;
  S.scale = s;
  win().style.transform = `scale(${s})`;
  stage.style.width = `${w * s}px`; stage.style.height = `${h * s}px`;
  stage.style.marginTop = `${Math.max(24, (desk.clientHeight - h * s) / 2)}px`;
}
window.addEventListener('resize', fit);

/* ================= 审阅面板 ================= */
// 第 1 轮审阅的结论（2026-10-02）。
const DECISIONS = [
  ['D1', '标题栏', '同意', 'common:snap'],
  ['D2', '控件风格', '按原版优化', 'page:library-filter'],
  ['D3', '内容层', '不加', 'page:home'],
  ['D4', '首页 hero 按钮', '不加', 'page:home'],
  ['D5', '播放页全屏优先', '同意', 'player:drawer'],
  ['D6', '转场', '同意', 'player:open'],
  ['D7', '设置页', '内容第 1 版，样式原版', 'page:settings-in'],
  ['D8', '详情页播放按钮', '同意', 'page:detail-series'],
  ['D9', '深色模式', '同意', 'page:settings-in'],
  ['D10', '应用图标', '暂缓', null],
  ['R1', '最近播放', '更紧凑', 'page:recent'],
  ['R2', '界面文案', '去掉说明文字', 'page:settings-out'],
  ['Q2', '选集抽屉', '两种都留', 'player:drawer'],
  ['Q3', '主题默认值', '跟随系统', 'page:settings-in'],
];
function rvBtn(label, act, active = false) { return `<button class="rv-btn ${active ? 'active' : ''}" data-rv="${act}">${label}</button>`; }
function renderReview() {
  $('#review').innerHTML = `
    <div class="rv-head"><h1>Mambo 设计稿 · P2 定稿</h1>${rvBtn('收起', 'collapse')}</div>
    <div class="rv-sec"><h2 class="rv-title">视图</h2>
      <div class="rv-row">${rvBtn('1500×860', 'size:large', S.size === 'large')}${rvBtn('1100×720（最小）', 'size:small', S.size === 'small')}</div>
      <div class="rv-row" style="margin-top:6px">${rvBtn('适应窗口', 'fit:on', S.fit)}${rvBtn('100%', 'fit:off', !S.fit)}${rvBtn('跟随系统', 'theme:system', S.themeMode === 'system')}${rvBtn('浅色', 'theme:light', S.themeMode === 'light')}${rvBtn('深色', 'theme:dark', S.themeMode === 'dark')}</div>
      <div class="rv-row" style="margin-top:6px">${rvBtn('显示改动标注', 'annotate', S.annotate)}${rvBtn('减少动画', 'motion', S.reduceMotion)}${rvBtn(S.loggedIn ? '已登录' : '未登录', 'login')}</div></div>
    <div class="rv-sec"><h2 class="rv-title">页面</h2><div class="rv-row">
      ${rvBtn('首页', 'page:home')}${rvBtn('首页·加载中', 'page:home-loading')}${rvBtn('首页·连接失败', 'page:home-error')}${rvBtn('未登录引导', 'page:onboard')}
      ${rvBtn('最近播放', 'page:recent')}${rvBtn('资料库', 'page:library')}${rvBtn('资料库·筛选', 'page:library-filter')}${rvBtn('资料库·无结果', 'page:library-empty')}
      ${rvBtn('电影详情', 'page:detail-movie')}${rvBtn('剧集详情', 'page:detail-series')}${rvBtn('搜索', 'page:search')}${rvBtn('设置·未登录', 'page:settings-out')}${rvBtn('设置·已登录', 'page:settings-in')}</div></div>
    <div class="rv-sec"><h2 class="rv-title">播放页</h2><div class="rv-row">
      ${rvBtn('打开播放', 'player:open')}${rvBtn('正在打开', 'player:opening')}${rvBtn('打开较慢（20 秒）', 'player:slow')}${rvBtn('控制层显示', 'player:chrome')}${rvBtn('控制层隐藏', 'player:hidden')}
      ${rvBtn('缓冲中', 'player:buffering')}${rvBtn('已暂停', 'player:paused')}${rvBtn('倍速菜单', 'player:speed')}${rvBtn('字幕/音轨菜单', 'player:tracks')}
      ${rvBtn('选集·列表', 'player:drawer')}${rvBtn('选集·集号', 'player:drawer-grid')}${rvBtn('片尾即将播放', 'player:upnext')}${rvBtn('播放失败', 'player:failed')}${rvBtn('全屏', 'player:fullscreen')}${rvBtn('关闭播放', 'player:close')}</div>
      <p class="rv-note">快捷键：<span class="rv-kbd">空格</span> 播放/暂停 · <span class="rv-kbd">←/→</span> ±5 秒 · <span class="rv-kbd">↑/↓</span> 音量 · <span class="rv-kbd">[ ]</span> 倍速 · <span class="rv-kbd">C</span> 字幕 · <span class="rv-kbd">V</span> 音轨 · <span class="rv-kbd">F</span> 全屏 · <span class="rv-kbd">Esc</span> 退出全屏/关闭</p></div>
    <div class="rv-sec"><h2 class="rv-title">通用</h2><div class="rv-row">${rvBtn('确认对话框', 'common:dialog')}${rvBtn('通知示例', 'common:toasts')}${rvBtn('错误通知', 'common:toast-error')}${rvBtn('贴靠布局', 'common:snap')}</div></div>
    <div class="rv-sec"><h2 class="rv-title">结论</h2>
      ${DECISIONS.map(([id, title, verdict, go]) => `<div class="rv-prop"><b>${id}</b><strong>${title}</strong><span class="rv-verdict">${verdict}</span>${go ? `<span class="rv-go">${rvBtn('查看', go)}</span>` : ''}</div>`).join('')}</div>`;
}
function playerState(kind) {
  const base = id => {
    if (!S.player || S._closing) openPlayer(id || S.player?.entries[S.player.index].id || 's1e1x3', { pinned: true });
    else resetPlayerRequest();
    S._entering = false;
    S.player.pinned = true;
    return S.player;
  };
  let p;
  switch (kind) {
    case 'open': {
      if (!S.player) { openPlayer('s1e1x3'); return; }
      const closed = closePlayer(), generation = playerGeneration;
      closed.then(completed => { if (completed && generation === playerGeneration && !S.player) openPlayer('s1e1x3'); });
      return;
    }
    case 'close': closePlayer(); return;
    case 'opening': p = base(); Object.assign(p, { phase: 'opening', slow: false, menu: null, drawer: false, upNext: false, chrome: true }); break;
    case 'slow': p = base(); Object.assign(p, { phase: 'opening', slow: true, menu: null, drawer: false, upNext: false, chrome: true }); break;
    case 'chrome': p = base(); Object.assign(p, { phase: 'playing', paused: false, buffering: false, menu: null, drawer: false, upNext: false, chrome: true }); break;
    case 'hidden': p = base(); Object.assign(p, { phase: 'playing', paused: false, buffering: false, menu: null, drawer: false, upNext: false, chrome: false }); break;
    case 'buffering': p = base(); Object.assign(p, { phase: 'playing', paused: false, buffering: true, menu: null, drawer: false, upNext: false, chrome: true }); break;
    case 'paused': p = base(); Object.assign(p, { phase: 'playing', paused: true, buffering: false, menu: null, drawer: false, upNext: false, chrome: true }); break;
    case 'speed': p = base(); Object.assign(p, { phase: 'playing', paused: false, buffering: false, menu: 'speed', drawer: false, upNext: false, chrome: true }); break;
    case 'tracks': p = base(); Object.assign(p, { phase: 'playing', paused: false, buffering: false, menu: 'tracks', drawer: false, upNext: false, chrome: true }); break;
    case 'drawer': p = base(); Object.assign(p, { phase: 'playing', paused: false, buffering: false, menu: null, drawer: true, drawerStyle: 'list', upNext: false, chrome: true }); break;
    case 'drawer-grid': p = base(); Object.assign(p, { phase: 'playing', paused: false, buffering: false, menu: null, drawer: true, drawerStyle: 'grid', upNext: false, chrome: true }); break;
    case 'upnext': p = base(); Object.assign(p, { phase: 'playing', paused: false, buffering: false, menu: null, drawer: false, upNext: true, chrome: true }); p.pos = p.entries[p.index].runtime - 15; break;
    case 'failed': p = base(); Object.assign(p, { phase: 'failed', menu: null, drawer: false, upNext: false, chrome: true }); break;
    case 'fullscreen': p = base(); Object.assign(p, { phase: 'playing', paused: false, menu: null, drawer: false, upNext: false, chrome: true, fullscreen: true }); break;
  }
  render({ page: false });
}
function pageState(kind) {
  resetPlayerRequest();
  S.player = null; S._closing = false; S._entering = false;
  S.homeVariant = 'normal'; S.libVariant = 'normal';
  const setFilters = (genre, decade) => { Object.values(S.filters).forEach(s => s.clear()); if (genre) S.filters.genre.add(genre); if (decade) S.filters.decade.add(decade); };
  const go = (name, params) => { S.route = { name, params: params || {} }; S.back = S.route.name === 'home' ? [] : [{ name: 'home', params: {}, scroll: 0 }]; S.fwd = []; };
  switch (kind) {
    case 'home': S.loggedIn = true; go('home'); break;
    case 'home-loading': S.loggedIn = true; S.homeVariant = 'loading'; go('home'); break;
    case 'home-error': S.loggedIn = true; S.homeVariant = 'error'; go('home'); break;
    case 'onboard': S.loggedIn = false; S.searchText = ''; go('home'); break;
    case 'recent': S.loggedIn = true; go('recent'); break;
    case 'library': S.loggedIn = true; S.filtersOpen = false; setFilters(); go('library', { lib: 'movies' }); break;
    case 'library-filter': S.loggedIn = true; S.filtersOpen = true; setFilters('科幻', '2020'); go('library', { lib: 'movies' }); break;
    case 'library-empty': S.loggedIn = true; S.filtersOpen = true; S.libVariant = 'empty'; setFilters('纪录', '2020'); go('library', { lib: 'movies' }); break;
    case 'detail-movie': S.loggedIn = true; go('detail', { id: 'm2' }); break;
    case 'detail-series': S.loggedIn = true; go('detail', { id: 's1' }); break;
    case 'search': S.loggedIn = true; S.searchText = '星'; S.searchMore = {}; go('search', { q: '星' }); break;
    case 'settings-out': S.loggedIn = false; S.searchText = ''; go('settings'); break;
    case 'settings-in': S.loggedIn = true; go('settings'); break;
  }
  render({ scroll: 0, enter: true });
}
$('#review').addEventListener('click', e => {
  const b = e.target.closest('[data-rv]'); if (!b) return;
  const [kind, arg] = b.dataset.rv.split(':');
  switch (kind) {
    case 'collapse': setReviewCollapsed(true, true); return;
    case 'size': S.size = arg; break;
    case 'fit': S.fit = arg === 'on'; break;
    case 'theme': S.themeMode = arg; S.theme = resolveTheme(); break;
    case 'annotate': S.annotate = !S.annotate; break;
    case 'motion': S.reduceMotion = !S.reduceMotion; applyMotionPreference(); renderReview(); return;
    case 'login': S.loggedIn = !S.loggedIn; if (!S.loggedIn) { S.route = { name: 'home', params: {} }; S.back = []; S.fwd = []; } break;
    case 'page': pageState(arg); return;
    case 'player': playerState(arg); return;
    case 'common':
      if (arg === 'dialog') openDialog({ title: '切换播放？', text: '当前播放将结束并保存进度。', confirm: '切换', danger: true });
      if (arg === 'toasts') { toast('warning', '有一集无法加入连播，已跳过。'); setTimeout(() => toast('success', '已连接到 demo.example。'), 250); setTimeout(() => toast('info', '字幕：简体中文'), 500); }
      if (arg === 'toast-error') toast('error', '播放失败：无法连接服务器。', '重试');
      if (arg === 'snap') { S.snapOpen = true; renderTitlebar(); }
      return;
  }
  render({ page: kind !== 'annotate' && kind !== 'motion' ? undefined : false, scroll: host().scrollTop });
});

/* 审阅面板可以收起；窗口较窄时自动收起，手动操作后不再自动切换。 */
let reviewManual = false;
function setReviewCollapsed(collapsed, manual = false) {
  if (manual) reviewManual = true;
  document.body.classList.toggle('rv-collapsed', collapsed);
  fit();
}
$('#rv-toggle').addEventListener('click', () => setReviewCollapsed(false, true));
const autoReview = () => { if (!reviewManual) setReviewCollapsed(window.innerWidth < 1000); };
window.addEventListener('resize', autoReview);

/* ================= 启动 ================= */
autoReview();
render({ scroll: 0, enter: true });
resetHeroTimer();
if (document.fonts?.ready) document.fonts.ready.then(fit);
