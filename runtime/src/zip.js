// zip.js — 极小 ZIP 写入器（只做 STORE，不压缩）
//
// 为什么自己写：PNG 序列要一次下载几百个文件，浏览器没有内置打包能力；
// 而运行时是零依赖的（要能塞进 .gha 的嵌入资源里），不能引 jszip。
// PNG 本身已经压缩过，再套 deflate 收益很小，所以只做 STORE（method=0）。

const CRC_TABLE = (() => {
  const t = new Uint32Array(256);
  for (let i = 0; i < 256; i++) {
    let c = i;
    for (let k = 0; k < 8; k++) c = c & 1 ? 0xEDB88320 ^ (c >>> 1) : c >>> 1;
    t[i] = c >>> 0;
  }
  return t;
})();

export function crc32(u8) {
  let c = 0xFFFFFFFF;
  for (let i = 0; i < u8.length; i++) c = CRC_TABLE[(c ^ u8[i]) & 0xFF] ^ (c >>> 8);
  return (c ^ 0xFFFFFFFF) >>> 0;
}

const SIG_LOCAL = 0x04034b50;
const SIG_CENTRAL = 0x02014b50;
const SIG_EOCD = 0x06054b50;
// 1980-01-01 00:00（DOS 时间）。固定值让同样的输入产出同样的字节，便于断言。
const DOS_TIME = 0;
const DOS_DATE = 0x0021;

/**
 * files: [{ name: string, data: Uint8Array }]
 * 返回 Uint8Array（可直接 new Blob([u8], {type:'application/zip'})）
 */
export function zipStore(files) {
  const enc = new TextEncoder();
  const entries = files.map((f) => {
    const name = enc.encode(f.name);
    return { name, data: f.data, crc: crc32(f.data) };
  });

  let localSize = 0;
  let centralSize = 0;
  for (const e of entries) {
    localSize += 30 + e.name.length + e.data.length;
    centralSize += 46 + e.name.length;
  }
  const out = new Uint8Array(localSize + centralSize + 22);
  const dv = new DataView(out.buffer);
  let p = 0;

  for (const e of entries) {
    e.offset = p;
    dv.setUint32(p, SIG_LOCAL, true); p += 4;
    dv.setUint16(p, 20, true); p += 2;              // version needed
    dv.setUint16(p, 0x0800, true); p += 2;          // flags: UTF-8 文件名
    dv.setUint16(p, 0, true); p += 2;               // method = STORE
    dv.setUint16(p, DOS_TIME, true); p += 2;
    dv.setUint16(p, DOS_DATE, true); p += 2;
    dv.setUint32(p, e.crc, true); p += 4;
    dv.setUint32(p, e.data.length, true); p += 4;   // compressed = raw（STORE）
    dv.setUint32(p, e.data.length, true); p += 4;
    dv.setUint16(p, e.name.length, true); p += 2;
    dv.setUint16(p, 0, true); p += 2;               // extra len
    out.set(e.name, p); p += e.name.length;
    out.set(e.data, p); p += e.data.length;
  }

  const cdStart = p;
  for (const e of entries) {
    dv.setUint32(p, SIG_CENTRAL, true); p += 4;
    dv.setUint16(p, 20, true); p += 2;              // version made by
    dv.setUint16(p, 20, true); p += 2;              // version needed
    dv.setUint16(p, 0x0800, true); p += 2;
    dv.setUint16(p, 0, true); p += 2;
    dv.setUint16(p, DOS_TIME, true); p += 2;
    dv.setUint16(p, DOS_DATE, true); p += 2;
    dv.setUint32(p, e.crc, true); p += 4;
    dv.setUint32(p, e.data.length, true); p += 4;
    dv.setUint32(p, e.data.length, true); p += 4;
    dv.setUint16(p, e.name.length, true); p += 2;
    dv.setUint16(p, 0, true); p += 2;               // extra
    dv.setUint16(p, 0, true); p += 2;               // comment
    dv.setUint16(p, 0, true); p += 2;               // disk
    dv.setUint16(p, 0, true); p += 2;               // internal attrs
    dv.setUint32(p, 0, true); p += 4;               // external attrs
    dv.setUint32(p, e.offset, true); p += 4;
    out.set(e.name, p); p += e.name.length;
  }

  const eocd = p;
  dv.setUint32(p, SIG_EOCD, true); p += 4;
  dv.setUint16(p, 0, true); p += 2;               // 本磁盘号
  dv.setUint16(p, 0, true); p += 2;               // 中央目录起始磁盘
  dv.setUint16(p, entries.length, true); p += 2;
  dv.setUint16(p, entries.length, true); p += 2;
  dv.setUint32(p, eocd - cdStart, true); p += 4;  // 中央目录字节数
  dv.setUint32(p, cdStart, true); p += 4;
  dv.setUint16(p, 0, true); p += 2;               // 注释长度
  return out;
}
