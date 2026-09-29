/* ===== 存储抽象层 =====
 * 设计：UI 与业务只依赖 adapter 接口（getAll / save / saveMany / remove），不关心底层。
 * 本文件提供两个实现，运行期自动探测选一个：
 *   · HttpFileAdapter —— 由「贴贴便签.exe」提供 /api/notes 接口，数据落盘为 exe 同级的 data/notes.json
 *                        （数据归程序管：关掉浏览器不丢、清浏览器数据也不丢，文件可直接备份/带走）
 *   · IndexedDBAdapter —— 直接双击 index.html 或其它静态服务下的回退方案（存在浏览器里，按 origin 隔离）
 * 将来阶段 2/3 换 SqliteAdapter（原生 SQLite）时，同样只需再加一个实现、字段保持一致。
 *
 * `note` 对象统一 camelCase：
 *   { id, title, content, color, done, pinned, archived, dueAt,
 *     tags:[], checklist:[{text,done}], createdAt, updatedAt, deletedAt?, syncStatus? }
 */

/* ---------- 字段级合并（S2，2026-09-20）----------
 * 背景：网页版把整库读进浏览器内存；桌面贴纸端在期间可能改了某条便签的 checklist[i].done。
 * 旧行为是「把内存里那一整条盖回磁盘」——贴纸刚勾上的会被无声抹掉。
 * 新行为：以**磁盘版为基**，只把本次**真正改动的字段**搬过去，其余字段一律保留磁盘上的值。
 *   · changedKeys = null        → 新便签，整条写入（保持旧语义）
 *   · "checklistDone:<i>"       → 这一项的 done 用内存版（用户刚点的就是它）
 *   · "checklist"               → 清单结构/文字用内存版；每一项的 done 仍优先取磁盘
 *   · 其它字段名                → 直接用内存版
 * 清单项按**文字匹配**（先同位置同名，再任意位置同名），匹配到的以磁盘项为基，保留其上的其它字段。
 */
function mergeChecklist(diskList, memList, doneIdx) {
  const out = [];
  const used = new Set();
  for (let i = 0; i < memList.length; i++) {
    const m = memList[i] || {};
    let d = null, di = -1;
    if (diskList[i] && diskList[i].text === m.text && !used.has(i)) { d = diskList[i]; di = i; }
    else {
      for (let j = 0; j < diskList.length; j++) {
        if (!used.has(j) && diskList[j] && diskList[j].text === m.text) { d = diskList[j]; di = j; break; }
      }
    }
    if (di >= 0) used.add(di);
    const item = d ? Object.assign({}, d) : {};
    item.text = m.text;
    item.done = doneIdx.has(i) ? !!doneIdx.get(i) : (d ? !!d.done : !!m.done);
    out.push(item);
  }
  return out;
}

function mergeNote(diskNote, memNote, changedKeys) {
  if (!memNote) return diskNote;
  if (!diskNote) return memNote;          // 新便签
  if (!changedKeys) return memNote;       // 调用方未声明改动范围 → 保持旧语义（整条替换）
  const keys = changedKeys || [];
  const doneIdx = new Map();
  for (const k of keys) {
    if (k.indexOf("checklistDone:") === 0) {
      const i = Number(k.slice("checklistDone:".length));
      if (memNote.checklist && memNote.checklist[i]) doneIdx.set(i, memNote.checklist[i].done);
    }
  }
  const out = Object.assign({}, diskNote);
  for (const k of keys) {
    if (k.indexOf("checklistDone:") === 0 || k === "checklist") continue;
    out[k] = memNote[k];
  }
  // 只有本次真的动了清单，才重建清单数组 —— 否则磁盘上新增的项绝不能被丢掉
  const touchesChecklist = keys.some((k) => k === "checklist" || k.indexOf("checklistDone:") === 0);
  if (touchesChecklist && memNote.checklist) {
    out.checklist = mergeChecklist(diskNote.checklist || [], memNote.checklist, doneIdx);
  }
  return out;
}

/* ---------- 实现一：启动器文件接口（推荐，exe 环境） ---------- */
class HttpFileAdapter {
  constructor() {
    this.API = "/api/notes";
    this._chain = Promise.resolve(); // 串行化写入，避免并发覆盖
  }

  async init() {
    // 探测阶段已用同一接口确认可用（openStore 的 fetch 成功才会走到这里），无需重复请求
  }

  async getAll() {
    const r = await fetch(this.API, { cache: "no-store" });
    if (!r.ok) throw new Error("读取失败：" + r.status);
    const arr = await r.json();
    return Array.isArray(arr) ? arr : [];
  }

  // 立即写整库（不再 120ms 合并：一次点击 = 一次落盘，关掉页面也不会丢）
  _writeNow(arr) {
    const run = this._chain.then(async () => {
      const r = await fetch(this.API, {
        method: "PATCH",   // 整库覆盖写（启动器同时接受 PUT / PATCH）
        headers: { "Content-Type": "application/json; charset=utf-8" },
        body: JSON.stringify(arr),
      });
      if (!r.ok) throw new Error("保存失败：" + r.status);
    });
    // 队列本身永不因一次失败而卡死；调用方仍然能看到这次失败
    this._chain = run.catch(() => {});
    return run;
  }

  /** 返回真正落盘的那条便签（合并后的形态），调用方据此刷新界面。 */
  async save(note, changedKeys) {
    const arr = await this.getAll();
    const i = arr.findIndex((n) => n.id === note.id);
    let merged;
    if (i >= 0) { merged = mergeNote(arr[i], note, changedKeys); arr[i] = merged; }
    else { merged = note; arr.push(merged); }
    await this._writeNow(arr);
    return merged;
  }

  async saveMany(list) {
    const arr = await this.getAll();
    for (const note of list) {
      const i = arr.findIndex((n) => n.id === note.id);
      if (i >= 0) arr[i] = note; else arr.push(note);
    }
    await this._writeNow(arr);
  }

  async remove(id) {
    const arr = await this.getAll();
    await this._writeNow(arr.filter((n) => n.id !== id));
  }
}

/* ---------- 实现二：IndexedDB（浏览器存储，回退方案） ---------- */
class IndexedDBAdapter {
  constructor() {
    this.db = null;
    this.DB_NAME = "tietie-notes";
    this.STORE = "notes";
    this.VERSION = 1;
  }

  init() {
    return new Promise((resolve, reject) => {
      const req = indexedDB.open(this.DB_NAME, this.VERSION);
      req.onupgradeneeded = (e) => {
        const db = e.target.result;
        if (!db.objectStoreNames.contains(this.STORE)) {
          db.createObjectStore(this.STORE, { keyPath: "id" });
        }
      };
      req.onsuccess = (e) => {
        this.db = e.target.result;
        resolve();
      };
      req.onerror = () => reject(req.error);
    });
  }

  _store(mode) {
    return this.db.transaction(this.STORE, mode).objectStore(this.STORE);
  }

  getAll() {
    return new Promise((resolve, reject) => {
      const req = this._store("readonly").getAll();
      req.onsuccess = () => resolve(req.result || []);
      req.onerror = () => reject(req.error);
    });
  }

  save(note, changedKeys) {
    return new Promise((resolve, reject) => {
      const req = this._store("readwrite").put(note);
      req.onsuccess = () => resolve(note);   // 单写者：无合并，原样写回
      req.onerror = () => reject(req.error);
    });
  }

  async saveMany(list) {
    for (const n of list) await this.save(n);
  }

  remove(id) {
    return new Promise((resolve, reject) => {
      const req = this._store("readwrite").delete(id);
      req.onsuccess = () => resolve();
      req.onerror = () => reject(req.error);
    });
  }
}

/* ---------- 探测：exe 文件模式优先，否则回退 IndexedDB ---------- */
async function openStore() {
  try {
    const r = await fetch("/api/notes", { cache: "no-store" });
    if (r.ok) {
      const a = new HttpFileAdapter();
      await a.init();
      return { store: a, mode: "file" };
    }
  } catch (e) {
    /* 没有启动器接口（比如直接双击 index.html），走回退 */
  }
  const a = new IndexedDBAdapter();
  await a.init();
  return { store: a, mode: "indexeddb" };
}

/* ---------- 一次性搬迁：把浏览器里的旧数据搬进数据文件 ---------- */
const MIGRATED_FLAG = "tietie-migrated-to-file";

function openLegacyIndexedDB() {
  return new Promise((resolve, reject) => {
    if (typeof indexedDB === "undefined") return reject(new Error("无 IndexedDB"));
    const a = new IndexedDBAdapter();
    a.init().then(() => a.getAll()).then(resolve).catch(reject);
  });
}

function migratedAlready() {
  try { return localStorage.getItem(MIGRATED_FLAG) === "1"; } catch (e) { return false; }
}

function markMigrated() {
  try { localStorage.setItem(MIGRATED_FLAG, "1"); } catch (e) { /* 忽略 */ }
}

async function migrateLegacyToFile(fileStore) {
  if (migratedAlready()) return 0;
  const existing = await fileStore.getAll();
  if (existing.length > 0) { markMigrated(); return 0; } // 文件里已有数据，不动
  let legacy = [];
  try { legacy = await openLegacyIndexedDB(); } catch (e) { return 0; }
  if (!legacy.length) { markMigrated(); return 0; }
  await fileStore.saveMany(legacy);
  markMigrated();
  return legacy.length;
}

function newId() {
  if (window.crypto && crypto.randomUUID) return crypto.randomUUID();
  return "n-" + Date.now() + "-" + Math.random().toString(36).slice(2, 9);
}

window.IndexedDBAdapter = IndexedDBAdapter;
window.HttpFileAdapter = HttpFileAdapter;
window.openStore = openStore;
window.migrateLegacyToFile = migrateLegacyToFile;
window.newId = newId;
window.mergeNote = mergeNote;
window.mergeChecklist = mergeChecklist;
