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

/* ---------- 实现一：启动器文件接口（推荐，exe 环境） ---------- */
class HttpFileAdapter {
  constructor() {
    this.API = "/api/notes";
    this._timer = null;
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

  // 立即写整库
  _writeNow(arr) {
    this._chain = this._chain.then(async () => {
      const r = await fetch(this.API, {
        method: "PATCH",   // 整库覆盖写（启动器同时接受 PUT / PATCH）
        headers: { "Content-Type": "application/json; charset=utf-8" },
        body: JSON.stringify(arr),
      });
      if (!r.ok) throw new Error("保存失败：" + r.status);
    });
    return this._chain;
  }

  // 合并连发写入：120ms 内的多次变更合并成一次落盘
  _schedule(arr) {
    if (this._timer) clearTimeout(this._timer);
    this._timer = setTimeout(() => {
      this._timer = null;
      this._writeNow(arr).catch((e) => console.error("[store] 落盘失败", e));
    }, 120);
  }

  async save(note) {
    const arr = await this.getAll();
    const i = arr.findIndex((n) => n.id === note.id);
    if (i >= 0) arr[i] = note; else arr.push(note);
    this._schedule(arr);
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
    this._schedule(arr.filter((n) => n.id !== id));
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

  save(note) {
    return new Promise((resolve, reject) => {
      const req = this._store("readwrite").put(note);
      req.onsuccess = () => resolve();
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
