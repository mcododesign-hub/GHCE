// signal.js — signals evaluated in the browser
//
// The GH side only sends the function (kind, seed, rate, coordinates), never samples.
// Every frame this file turns each definition into a number, and main.js reports the values
// back inside `stat`. That is the whole loop: no protocol field on commands, no rendering hook.
//
// Why bother when the GH components already compute the same thing:
//   GH components   30 Hz, zero latency, but every downstream component re-solves
//   these signals   60 Hz, GH does nothing, values arrive back with about 100 ms of lag

const clamp01 = (v) => (v < 0 ? 0 : v > 1 ? 1 : v);

/* ---------------- Perlin improved noise ---------------- */

const PERM_CACHE = new Map();

function buildPerm(seed) {
  const key = seed | 0;
  const hit = PERM_CACHE.get(key);
  if (hit) return hit;

  const base = new Uint8Array(256);
  for (let i = 0; i < 256; i++) base[i] = i;

  let s = key || 1;
  const rnd = () => {
    s ^= s << 13; s >>>= 0;
    s ^= s >>> 17;
    s ^= s << 5; s >>>= 0;
    return s / 4294967296;
  };

  for (let i = 255; i > 0; i--) {
    const j = Math.floor(rnd() * (i + 1));
    const t = base[i]; base[i] = base[j]; base[j] = t;
  }

  const p = new Uint8Array(512);
  for (let i = 0; i < 512; i++) p[i] = base[i & 255];

  if (PERM_CACHE.size > 64) PERM_CACHE.clear();
  PERM_CACHE.set(key, p);
  return p;
}

const fade = (t) => t * t * t * (t * (t * 6 - 15) + 10);
const lerp = (a, b, t) => a + (b - a) * t;

function grad3(hash, x, y, z) {
  const h = hash & 15;
  const u = h < 8 ? x : y;
  const v = h < 4 ? y : (h === 12 || h === 14 ? x : z);
  return ((h & 1) ? -u : u) + ((h & 2) ? -v : v);
}

/** 3D Perlin, roughly -1..1. 1D and 2D reuse it by fixing the unused axes. */
function noise3(p, x, y, z) {
  const fx = Math.floor(x), fy = Math.floor(y), fz = Math.floor(z);
  const X = fx & 255, Y = fy & 255, Z = fz & 255;
  x -= fx; y -= fy; z -= fz;

  const u = fade(x), v = fade(y), w = fade(z);
  const A = p[X] + Y, AA = p[A] + Z, AB = p[A + 1] + Z;
  const B = p[X + 1] + Y, BA = p[B] + Z, BB = p[B + 1] + Z;

  return lerp(
    lerp(
      lerp(grad3(p[AA], x, y, z), grad3(p[BA], x - 1, y, z), u),
      lerp(grad3(p[AB], x, y - 1, z), grad3(p[BB], x - 1, y - 1, z), u),
      v,
    ),
    lerp(
      lerp(grad3(p[AA + 1], x, y, z - 1), grad3(p[BA + 1], x - 1, y, z - 1), u),
      lerp(grad3(p[AB + 1], x, y - 1, z - 1), grad3(p[BB + 1], x - 1, y - 1, z - 1), u),
      v,
    ),
    w,
  );
}

/** Integer to 0..1. Same step yields the same value, so `random` holds between steps. */
function hash01(i) {
  let h = Math.imul(i ^ 0x9e3779b9, 0x85ebca6b);
  h ^= h >>> 13;
  h = Math.imul(h, 0xc2b2ae35);
  h ^= h >>> 16;
  return (h >>> 0) / 4294967296;
}

/* ---------------- evaluation ---------------- */

/**
 * Value of a signal at `time` (seconds), normalised to 0..1.
 *
 * @param sig  { kind, seed, rate, args }
 * @param time seconds on the runtime clock
 */
export function signalValue(sig, time) {
  if (!sig) return 0.5;

  const rate = sig.rate > 0 ? sig.rate : 1;
  const seed = sig.seed | 0;
  const a = Number.isFinite(sig.args?.[0]) ? sig.args[0] : 0;
  const b = Number.isFinite(sig.args?.[1]) ? sig.args[1] : 0;
  const c = Number.isFinite(sig.args?.[2]) ? sig.args[2] : 0;

  switch (sig.kind) {
    case 'random':
      return hash01(Math.floor(time * rate) + seed * 7919);

    case 'noise2':
      return (noise3(buildPerm(seed), a + time * rate, b, 0.5) + 1) * 0.5;

    case 'noise3':
      return (noise3(buildPerm(seed), a + time * rate, b, c) + 1) * 0.5;

    case 'noise1':
    default:
      return clamp01((noise3(buildPerm(seed), a + time * rate, 0.5, 0.5) + 1) * 0.5);
  }
}

/** Every registered signal, evaluated for the current instant. Sent back inside `stat`. */
export function signalSnapshot(signals, time) {
  const out = {};
  if (!signals) return out;
  for (const [id, def] of signals) out[id] = +signalValue(def, time).toFixed(4);
  return out;
}
