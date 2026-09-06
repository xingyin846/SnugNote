/* ===== 存储抽象层 + IndexedDB 实现 =====
 * 设计：UI 与业务只依赖 adapter 接口；阶段 1 用 IndexedDB，阶段 2/3 换成 SqliteAdapter（字段不变）。
 * `note` 对象统一 camelCase：
 *   { id, title, content, color, done, pinned, archived, dueAt,
 *     tags:[], checklist:[{text,done}], createdAt, updatedAt, deletedAt?, syncStatus? }
 */

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

  remove(id) {
    return new Promise((resolve, reject) => {
      const req = this._store("readwrite").delete(id);
      req.onsuccess = () => resolve();
      req.onerror = () => reject(req.error);
    });
  }
}

function newId() {
  if (window.crypto && crypto.randomUUID) return crypto.randomUUID();
  return "n-" + Date.now() + "-" + Math.random().toString(36).slice(2, 9);
}

window.IndexedDBAdapter = IndexedDBAdapter;
window.newId = newId;
