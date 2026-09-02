const fs = require("fs");
const path = require("path");

function extractAscii(buf, minLen = 4) {
  const out = [];
  let cur = [];
  for (let i = 0; i < buf.length; i++) {
    const c = buf[i];
    if (c >= 32 && c <= 126) cur.push(String.fromCharCode(c));
    else {
      if (cur.length >= minLen) out.push(cur.join(""));
      cur = [];
    }
  }
  if (cur.length >= minLen) out.push(cur.join(""));
  return out;
}

function extractUtf16le(buf, minLen = 4) {
  const out = [];
  let cur = [];
  for (let i = 0; i + 1 < buf.length; i += 2) {
    const c = buf[i] | (buf[i + 1] << 8);
    if (c >= 32 && c <= 126) cur.push(String.fromCharCode(c));
    else {
      if (cur.length >= minLen) out.push(cur.join(""));
      cur = [];
    }
  }
  if (cur.length >= minLen) out.push(cur.join(""));
  return out;
}

const KEYWORDS = /tool|mill|drill|bit|profile|clearance|pocket|feed|plunge|spindle|rpm|stepover|depth|material|thickness|router|end mill|v-bit|cutter|pass|ramp|lead|offset|contour|nest|sheet|mm |diameter|flute|safe|home|post/i;

function uniqueKeepOrder(arr) {
  const seen = new Set();
  const out = [];
  for (const s of arr) {
    const t = s.trim();
    if (!t || seen.has(t)) continue;
    seen.add(t);
    out.push(t);
  }
  return out;
}

function analyzeArt(file) {
  const buf = fs.readFileSync(file);
  const ascii = extractAscii(buf, 5);
  const utf16 = extractUtf16le(buf, 5);
  const interesting = uniqueKeepOrder(
    [...ascii, ...utf16].filter((s) => KEYWORDS.test(s) || /T\d+|S\d{3,}|F\d{3,}/.test(s))
  );
  const allShort = uniqueKeepOrder(
    [...ascii, ...utf16].filter((s) => s.length >= 6 && s.length <= 80)
  );
  return {
    file,
    name: path.basename(file),
    bytes: buf.length,
    magic: buf.slice(0, 16).toString("hex"),
    asciiHead: buf.slice(0, 32).toString("latin1").replace(/[^\x20-\x7e]/g, "."),
    interesting,
    sampleStrings: allShort.slice(0, 80),
    toolish: uniqueKeepOrder(
      [...ascii, ...utf16].filter((s) =>
        /mm|Mill|Drill|Bit|Tool|End |Router|V-|cutter|flute|Ø|dia/i.test(s)
      )
    ).slice(0, 60),
  };
}

function parseGcode(file) {
  const text = fs.readFileSync(file, "utf8");
  const lines = text.split(/\r?\n/);
  const tools = new Set();
  const spindles = new Set();
  const feeds = new Set();
  const zCuts = [];
  const zSafes = [];
  const headers = [];
  const mCodes = new Set();
  let currentTool = null;
  let currentS = null;
  let currentF = null;
  let minZ = Infinity;
  let maxZ = -Infinity;
  let xyMoves = 0;
  let g0 = 0,
    g1 = 0,
    g2 = 0,
    g3 = 0;
  let hasRamp = false;
  let lastZ = null;
  const zByTool = {};
  const fByTool = {};
  const sByTool = {};
  const segments = [];
  let seg = null;

  const startSeg = () => {
    if (seg) segments.push(seg);
    seg = {
      tool: currentTool,
      spindle: currentS,
      feeds: new Set(),
      zMin: Infinity,
      zMax: -Infinity,
      g1: 0,
      g0: 0,
    };
  };

  for (const raw of lines.slice(0, 25)) {
    const t = raw.trim();
    if (t.startsWith(";") || t.startsWith("(") || t.startsWith("%") || t.startsWith("@"))
      headers.push(t.slice(0, 160));
  }

  for (const raw of lines) {
    const line = raw.toUpperCase();
    const tMatch = line.match(/\bT(\d+)\b/) || line.match(/M6\s*T(\d+)/);
    if (tMatch) {
      currentTool = "T" + tMatch[1];
      tools.add(currentTool);
      startSeg();
    }
    const sMatch = line.match(/\bS(\d+(?:\.\d+)?)/);
    if (sMatch) {
      currentS = Number(sMatch[1]);
      spindles.add(currentS);
      if (currentTool) {
        sByTool[currentTool] = sByTool[currentTool] || new Set();
        sByTool[currentTool].add(currentS);
      }
    }
    const fMatch = line.match(/\bF(\d+(?:\.\d+)?)/);
    if (fMatch) {
      currentF = Number(fMatch[1]);
      feeds.add(currentF);
      if (currentTool) {
        fByTool[currentTool] = fByTool[currentTool] || new Set();
        fByTool[currentTool].add(currentF);
      }
      if (seg) seg.feeds.add(currentF);
    }
    const zMatch = line.match(/\bZ(-?\d+(?:\.\d+)?)/);
    if (zMatch) {
      const z = Number(zMatch[1]);
      minZ = Math.min(minZ, z);
      maxZ = Math.max(maxZ, z);
      if (z < 0.5) zCuts.push(z);
      if (z >= 8) zSafes.push(z);
      if (lastZ != null && z < lastZ - 0.2 && /G1/.test(line) && /X|Y/.test(line) && zMatch)
        hasRamp = true;
      lastZ = z;
      if (currentTool) {
        zByTool[currentTool] = zByTool[currentTool] || { min: Infinity, max: -Infinity };
        zByTool[currentTool].min = Math.min(zByTool[currentTool].min, z);
        zByTool[currentTool].max = Math.max(zByTool[currentTool].max, z);
      }
      if (seg) {
        seg.zMin = Math.min(seg.zMin, z);
        seg.zMax = Math.max(seg.zMax, z);
      }
    }
    if (/\bG0\b|\bG00\b/.test(line)) {
      g0++;
      if (seg) seg.g0++;
    }
    if (/\bG1\b|\bG01\b/.test(line)) {
      g1++;
      xyMoves++;
      if (seg) seg.g1++;
    }
    if (/\bG2\b|\bG02\b/.test(line)) g2++;
    if (/\bG3\b|\bG03\b/.test(line)) g3++;
    const mm = line.match(/\bM\d+\b/g);
    if (mm) mm.forEach((m) => mCodes.add(m));
  }
  if (seg) segments.push(seg);

  const uniqueSorted = (arr) => [...new Set(arr.map((n) => Math.round(n * 1000) / 1000))].sort((a, b) => a - b);

  function setToArr(s) {
    return [...s].sort((a, b) => a - b);
  }

  const toolSummary = [...tools].sort().map((t) => ({
    tool: t,
    spindle: sByTool[t] ? setToArr(sByTool[t]) : [],
    feeds: fByTool[t] ? setToArr(fByTool[t]) : [],
    zMin: zByTool[t] ? zByTool[t].min : null,
    zMax: zByTool[t] ? zByTool[t].max : null,
  }));

  return {
    file,
    name: path.basename(file),
    bytes: fs.statSync(file).size,
    lines: lines.length,
    tools: [...tools],
    spindles: setToArr(spindles),
    feeds: setToArr(feeds),
    zMin: Number.isFinite(minZ) ? minZ : null,
    zMax: Number.isFinite(maxZ) ? maxZ : null,
    zCutsUnique: uniqueSorted(zCuts).slice(0, 30),
    zSafesUnique: uniqueSorted(zSafes).slice(0, 20),
    g0,
    g1,
    g2,
    g3,
    hasRamp,
    hasArcs: g2 + g3 > 0,
    mCodes: [...mCodes].sort(),
    headers: headers.slice(0, 12),
    toolSummary,
    firstLines: lines.slice(0, 22).map((l) => l.trim()).filter(Boolean),
    lastLines: lines.slice(-12).map((l) => l.trim()).filter(Boolean),
  };
}

function walk(dir, exts, acc = []) {
  if (!fs.existsSync(dir)) return acc;
  for (const ent of fs.readdirSync(dir, { withFileTypes: true })) {
    const p = path.join(dir, ent.name);
    try {
      if (ent.isDirectory()) {
        if (/node_modules|Artcam2018|WeChat Files|AppData/i.test(p)) continue;
        walk(p, exts, acc);
      } else if (exts.includes(path.extname(ent.name).toLowerCase())) acc.push(p);
    } catch (_) {}
  }
  return acc;
}

const artFiles = [
  "C:\\Users\\user\\Desktop\\Rouge_232_BK_15mm.art",
  "C:\\Users\\user\\Desktop\\Rouge_232_BK_18mm.art",
  "C:\\Users\\user\\Documents\\bedroom shelf.art",
  "C:\\Users\\user\\Documents\\cesar door.art",
];
for (const p of walk("E:\\Work\\Rouge", [".art"])) artFiles.push(p);

const ncFiles = [
  ...walk("C:\\Users\\user\\Documents", [".cnc"]).filter((p) => /cesar|shelf/i.test(p)),
  ...walk("C:\\Users\\user\\Desktop\\Default", [".nc"]),
  ...walk("E:\\Work\\CNC software\\XML\\加工26-0709\\加工中心", [".nc"]),
];

const arts = [];
for (const f of uniqueKeepOrder(artFiles)) {
  if (fs.existsSync(f)) {
    try {
      arts.push(analyzeArt(f));
    } catch (e) {
      arts.push({ file: f, error: String(e) });
    }
  }
}

const ncs = [];
for (const f of uniqueKeepOrder(ncFiles)) {
  if (fs.existsSync(f)) {
    try {
      ncs.push(parseGcode(f));
    } catch (e) {
      ncs.push({ file: f, error: String(e) });
    }
  }
}

const out = { arts, ncs };
const outPath = "E:\\Work\\cabinetnc-cut\\tmp_artcam_reverse.json";
fs.writeFileSync(outPath, JSON.stringify(out, null, 2));
console.log("arts", arts.length, "ncs", ncs.length, "->", outPath);
for (const a of arts) {
  console.log("\nART", a.name, a.bytes, a.asciiHead);
  console.log("  toolish", (a.toolish || []).slice(0, 25).join(" | "));
  console.log("  interesting", (a.interesting || []).slice(0, 30).join(" | "));
}
for (const n of ncs) {
  console.log(
    "NC",
    n.name,
    "T",
    (n.tools || []).join(","),
    "S",
    (n.spindles || []).join(","),
    "F",
    (n.feeds || []).join(","),
    "Z",
    n.zMin,
    "..",
    n.zMax,
    "ramp",
    n.hasRamp,
    "arcs",
    n.hasArcs
  );
}
