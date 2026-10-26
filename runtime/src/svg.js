// svg.js — 矢量后端（阶段 1 验收项「同一 Draw Command 生成 PNG 和基础 SVG」的提前实现）
// 原则：能在 SVG 里表达的保持矢量；不能表达的（blend / mask）不静默丢弃，写入 <desc> 与导出报告

import { flatCoords } from './geom.js';
import { resolveMatrix, resolveMorph, morphFallbackU, trackU } from './xform.js';

/** morph 在当前时刻的形态 → SVG path d。导出即"这一帧的样子"，不是第一帧。 */
function morphPathData(cmd, doc) {
  const m = cmd.morph;
  if (!m) return null;
  const u = cmd.track
    ? trackU(cmd.track, doc.time || 0, doc.timeline)
    : morphFallbackU(m, doc.time || 0);
  const pts = resolveMorph(m, u);
  if (!pts || pts.length < 4) return null;

  let d = `M${num(pts[0])},${num(pts[1])}`;
  for (let i = 2; i < pts.length; i += 2) d += `L${num(pts[i])},${num(pts[i + 1])}`;
  return m.closed ? d + 'Z' : d;
}

const esc = (s) => String(s).replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/"/g, '&quot;');
const num = (v) => (Math.round(v * 1000) / 1000).toString();

function transformAttr(t) {
  if (!t) return '';
  const parts = [];
  // SVG 的 matrix(a,b,c,d,e,f) 与 Canvas 2D 同序。progress < 1 时要和画布用同一个插值结果，
  // 否则"导出的 SVG 和屏幕上的位置不一样"。
  const m = resolveMatrix(t);
  if (m) parts.push(`matrix(${m.map(num).join(',')})`);
  if (t.translate[0] || t.translate[1]) parts.push(`translate(${num(t.translate[0])},${num(t.translate[1])})`);
  const px = t.pivot ? t.pivot[0] : 0;
  const py = t.pivot ? t.pivot[1] : 0;
  // 协议里 rotate 已经是「度」。早期这里多乘了 180/π，导出的旋转会大 57 倍
  if (t.rotate) parts.push(`rotate(${num(t.rotate)},${num(px)},${num(py)})`);
  if (t.scale[0] !== 1 || t.scale[1] !== 1) {
    parts.push(`translate(${num(px)},${num(py)}) scale(${num(t.scale[0])},${num(t.scale[1])}) translate(${num(-px)},${num(-py)})`);
  }
  return parts.join(' ');
}

function styleAttrs(style, warnings) {
  const a = [];
  const st = style.stroke, fi = style.fill;
  if (fi.color) {
    a.push(`fill="${esc(fi.color)}"`);
    if (fi.alpha !== 1) a.push(`fill-opacity="${num(fi.alpha)}"`);
    if (fi.rule === 'evenodd') a.push('fill-rule="evenodd"');
  } else a.push('fill="none"');
  if (st.color && st.width > 0) {
    a.push(`stroke="${esc(st.color)}"`, `stroke-width="${num(st.width)}"`);
    if (st.alpha !== 1) a.push(`stroke-opacity="${num(st.alpha)}"`);
    if (st.cap && st.cap !== 'butt') a.push(`stroke-linecap="${st.cap}"`);
    if (st.join && st.join !== 'miter') a.push(`stroke-linejoin="${st.join}"`);
    if (Array.isArray(st.dash) && st.dash.length) a.push(`stroke-dasharray="${st.dash.map(num).join(' ')}"`);
  }
  return a.join(' ');
}

function pathData(geom) {
  if (geom.kind === 'path') {
    return geom.cmds
      .map((c) => {
        const op = c[0];
        if (op === 'Z') return 'Z';
        return op + c.slice(1).map(num).join(' ');
      })
      .join(' ');
  }
  const f = flatCoords(geom);
  if (!f || f.length < 4) return null;
  let d = `M${num(f[0])} ${num(f[1])}`;
  for (let i = 2; i < f.length; i += 2) d += ` L${num(f[i])} ${num(f[i + 1])}`;
  if (geom.kind === 'polygon') d += ' Z';
  return d;
}

/** 生成 SVG 文本。返回 { svg, report } */
export function toSVG(doc) {
  const { w, h, bg } = doc.canvas;
  const warnings = [];
  const body = [];
  // 裁剪要写成 <clipPath> 定义，再在元素上引用 —— SVG 没有 canvas 的 clip() 即时状态
  const defs = [];
  let clipSeq = 0;

  if (bg) body.push(`<rect x="0" y="0" width="${w}" height="${h}" fill="${esc(bg)}"/>`);

  let drawn = 0;
  for (const cmd of doc.drawList()) {
    if (!cmd.visible) continue;
    const geom = doc.geoms.get(cmd.geom);
    if (!geom) { warnings.push(`cmd ${cmd.id}: 引用缺失几何 ${cmd.geom}`); continue; }
    const layer = cmd.layer ? doc.layers.get(cmd.layer) : null;
    if (layer && layer.visible === false) continue;

    if (layer && layer.blend && layer.blend !== 'source-over') {
      warnings.push(`layer ${layer.id}: blend=${layer.blend} 已降级为 source-over（SVG 需 filter，V0.1 未实现）`);
    }
    if (layer && layer.mask) {
      warnings.push(`layer ${layer.id}: mask 未实现，已忽略`);
    }

    const tf = transformAttr(cmd.transform);
    const opaque = (layer ? (layer.opacity ?? 1) : 1) * (cmd.opacity ?? 1);
    const wrap = [];
    if (tf) wrap.push(`transform="${tf}"`);

    if (cmd.clip) {
      const cg = doc.geoms.get(cmd.clip.geom);
      const cd = cg && cg.kind !== 'image' ? pathData(cg) : null;
      if (!cd) {
        warnings.push(`cmd ${cmd.id}: 裁剪轮廓缺失或不可矢量化，已忽略`);
      } else {
        const id = `clip${clipSeq++}`;
        const d = cmd.clip.invert
          ? `M0 0 H${w} V${h} H0 Z ${cd}`
          : cd;
        defs.push(
          `<clipPath id="${id}"><path d="${d}" clip-rule="${cmd.clip.invert ? 'evenodd' : 'nonzero'}"/></clipPath>`);
        wrap.push(`clip-path="url(#${id})"`);
      }
    }
    if (opaque !== 1) wrap.push(`opacity="${num(opaque)}"`);
    if (layer && layer.name) wrap.push(`data-layer="${esc(layer.name)}"`);
    wrap.push(`data-cmd="${esc(cmd.id)}"`);

    if (geom.kind === 'image') {
      // 几何是单位方块，尺寸与位置全在矩阵里 —— 与 canvas 端同一套语义
      body.push(
        `<image width="1" height="1" preserveAspectRatio="none" href="${esc(geom.src)}" ${wrap.join(' ')}/>`);
      drawn++;
      continue;
    }

    if (geom.kind === 'points') {
      const f = flatCoords(geom);
      const rr = Math.max(0.5, (cmd.style.pointSize || 3) / 2);
      const col = cmd.style.fill.color || cmd.style.stroke.color || '#fff';
      for (let i = 0; i < f.length; i += 2) {
        body.push(`<circle cx="${num(f[i])}" cy="${num(f[i + 1])}" r="${num(rr)}" fill="${esc(col)}" ${wrap.join(' ')}/>`);
      }
      drawn++;
      continue;
    }

    const d = cmd.morph ? morphPathData(cmd, doc) : pathData(geom);
    if (!d) { warnings.push(`cmd ${cmd.id}: 几何无法矢量化`); continue; }
    body.push(`<path d="${d}" ${styleAttrs(cmd.style, warnings)} ${wrap.join(' ')}/>`);
    drawn++;
  }

  const svg = [
    `<?xml version="1.0" encoding="UTF-8"?>`,
    `<svg xmlns="http://www.w3.org/2000/svg" width="${w}" height="${h}" viewBox="0 0 ${w} ${h}">`,
    `<title>GHCE Artwork rev ${doc.rev}</title>`,
    `<desc>generated by GHCE runtime · ${drawn} draw commands${warnings.length ? ' · ' + warnings.length + ' degraded' : ''}</desc>`,
    ...(defs.length ? [`<defs>${defs.join('')}</defs>`] : []),
    ...body,
    `</svg>`,
  ].join('\n');

  return { svg, report: { drawn, warnings } };
}
