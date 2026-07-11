// Regenerates the shipped Material Design Icons font as a minimal subset containing
// ONLY the glyphs referenced in MiniIde source. Building a fresh font from the used
// glyphs is what keeps it tiny AND drops MDI's ~6,900-ligature GSUB table — the thing
// that makes the full font take ~12-40s to load (see the icon-font investigation).
//
// Source of truth for "which glyphs":  every `"\U000FXXXX"` escape in src/MiniIde/**/*.cs
// (ActionIcon, FileIcon, ...). Add an icon there, then run:  npm run subset  (in tools/)
//
// The MiniIde.Tests guard test fails if a referenced glyph is missing from the shipped
// font, so a forgotten re-run is caught loudly instead of rendering as a blank tofu box.

import { readFileSync, writeFileSync, readdirSync, statSync } from 'node:fs';
import { join, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';
import opentype from 'opentype.js';

const here = dirname(fileURLToPath(import.meta.url));
const repo = join(here, '..');
const FULL_FONT = join(here, 'fonts', 'MaterialDesignIconsDesktop.full.ttf'); // source (not shipped)
const OUT_FONT  = join(repo, 'src', 'MiniIde', 'Assets', 'icons', 'MaterialDesignIconsDesktop.ttf'); // shipped
const SCAN_DIR  = join(repo, 'src', 'MiniIde');
const FAMILY    = 'Material Design Icons Desktop'; // MUST match App.axaml's FontFamily "#..." suffix

// --- 1. collect MDI codepoints from all .cs files ---------------------------------------
function csFiles(dir) {
  const out = [];
  for (const name of readdirSync(dir)) {
    if (name === 'bin' || name === 'obj') continue;
    const p = join(dir, name);
    if (statSync(p).isDirectory()) out.push(...csFiles(p));
    else if (name.endsWith('.cs')) out.push(p);
  }
  return out;
}

const escape = /\\U([0-9A-Fa-f]{8})/g; // C# supplementary-plane escape, e.g. \U000F0450
const codepoints = new Set();
for (const file of csFiles(SCAN_DIR)) {
  for (const m of readFileSync(file, 'utf8').matchAll(escape)) {
    const cp = parseInt(m[1], 16);
    if (cp >= 0xF0000 && cp <= 0xFFFFF) codepoints.add(cp); // MDI lives in Supplementary PUA-A
  }
}
const cps = [...codepoints].sort((a, b) => a - b);
if (cps.length === 0) {
  console.error('No MDI codepoints (\\U000FXXXX) found under src/MiniIde — refusing to write an empty font.');
  process.exit(1);
}

// Drop the given sfnt tables and return a fresh, well-formed buffer. MDI's giant GSUB
// ligature table trips every font parser (opentype.js, harfbuzz), so we remove the layout
// tables before parsing. Checksums are left zero — opentype.js re-emits the whole font.
function stripTables(buf, drop) {
  const dv = new DataView(buf.buffer, buf.byteOffset, buf.byteLength);
  const recs = [];
  for (let i = 0, n = dv.getUint16(4); i < n; i++) {
    const o = 12 + i * 16;
    const tag = String.fromCharCode(buf[o], buf[o + 1], buf[o + 2], buf[o + 3]);
    if (drop.has(tag)) continue;
    const offset = dv.getUint32(o + 8), length = dv.getUint32(o + 12);
    recs.push({ tag, length, data: buf.subarray(offset, offset + length) });
  }
  recs.sort((a, b) => (a.tag < b.tag ? -1 : a.tag > b.tag ? 1 : 0));
  const n = recs.length;
  const pad = (x) => (x + 3) & ~3;
  let cursor = 12 + n * 16;
  for (const r of recs) { r.offset = cursor; cursor += pad(r.length); }
  const out = Buffer.alloc(cursor);
  buf.copy(out, 0, 0, 4); // sfntVersion
  const odv = new DataView(out.buffer, out.byteOffset, out.byteLength);
  const exp = Math.floor(Math.log2(n)), pow = 1 << exp;
  odv.setUint16(4, n); odv.setUint16(6, pow * 16); odv.setUint16(8, exp); odv.setUint16(10, n * 16 - pow * 16);
  let d = 12;
  for (const r of recs) {
    out.write(r.tag, d, 'latin1');
    odv.setUint32(d + 8, r.offset); odv.setUint32(d + 12, r.length);
    r.data.copy(out, r.offset);
    d += 16;
  }
  return out;
}

// --- 2. build a fresh font from just those glyphs (no GSUB, cmap keyed by codepoint) -----
const buf = readFileSync(FULL_FONT);
const clean = stripTables(buf, new Set(['GSUB', 'GPOS', 'GDEF']));
const full = opentype.parse(clean.buffer.slice(clean.byteOffset, clean.byteOffset + clean.byteLength));

const hex = (c) => 'U+' + c.toString(16).toUpperCase();
const notdef = full.glyphs.get(0); // index 0 must be .notdef
const glyphs = [notdef];
const seen = new Set([0]);
const missing = [];
for (const cp of cps) {
  const gi = full.charToGlyphIndex(String.fromCodePoint(cp));
  if (!gi) { missing.push(cp); continue; } // 0 => .notdef => not in source font
  if (seen.has(gi)) continue;
  seen.add(gi);
  const g = full.glyphs.get(gi);
  g.unicode = cp;
  g.unicodes = [cp];
  glyphs.push(g);
}
if (missing.length) {
  console.error(`These codepoints are not in the full font (${FULL_FONT}): ${missing.map(hex).join(' ')}`);
  console.error('Wrong codepoint, or the pinned MDI release lacks them. Aborting.');
  process.exit(1);
}

const subset = new opentype.Font({
  familyName: FAMILY,
  styleName: 'Regular',
  unitsPerEm: full.unitsPerEm,
  ascender: full.ascender,
  descender: full.descender,
  glyphs,
});
writeFileSync(OUT_FONT, Buffer.from(subset.toArrayBuffer()));

const outKB = (statSync(OUT_FONT).size / 1024).toFixed(1);
console.log(`Subset ${cps.length} glyph(s): ${cps.map(hex).join(' ')}`);
console.log(`Family "${FAMILY}"  ->  ${OUT_FONT}`);
console.log(`Size ${(buf.length / 1024 / 1024).toFixed(2)} MB (full)  ->  ${outKB} KB (shipped)`);
