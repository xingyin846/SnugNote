/* ===== 贴贴便签 · 电脑版（本地文件持久化 / IndexedDB 回退 + JSON 导出/导入） ===== */

const COLORS = [
  { key: "yellow", hex: "#fff6c9" },
  { key: "pink",   hex: "#ffe3ee" },
  { key: "blue",   hex: "#dcefff" },
  { key: "green",  hex: "#e3f6d0" },
  { key: "purple", hex: "#ece3ff" },
  { key: "orange", hex: "#ffe8d3" },
];


/* ---------- 状态 ---------- */
let state = { nav: "all", sort: "pin", query: "", tags: [] }; // tags：多选标签，空数组 = 不筛
let editingId = null; // null = 新建
let notes = [];       // 从 store 加载
let store = null;     // 启动时由 openStore() 探测：exe 文件模式 / IndexedDB 回退
let storeMode = "";   // "file" = 数据落盘为 data/notes.json；"indexeddb" = 浏览器存储

const $ = (s) => document.querySelector(s);
const board = $("#board");

/* ---------- 工具 ---------- */
function today() { return new Date().toISOString().slice(0, 10); }
function nextDay(offset) { const d = new Date(); d.setDate(d.getDate() + offset); return d.toISOString().slice(0, 10); }
function dateLabel(due) {
  if (!due) return null;
  const t = today();
  if (due === t) return { text: "今天", cls: "today" };
  if (due < t) return { text: "已逾期", cls: "overdue" };
  return { text: due, cls: "" };
}
function escapeHtml(s) {
  return String(s || "").replace(/[&<>"']/g, (c) => ({
    "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;",
  }[c]));
}
// 标签配色：按名字哈希出固定色相（同名标签永远同色）。选中时用更深、更饱和的一档。
function tagHue(name) {
  let h = 0;
  for (const c of String(name)) h = (h * 31 + c.charCodeAt(0)) % 360;
  return h;
}

/* ---------- 渲染 ---------- */
function visibleNotes() {
  let list = notes.filter((n) => {
    if (n.archived !== (state.nav === "archived")) return false;
    if (state.nav === "todo" && n.done) return false;
    if (state.nav === "done" && !n.done) return false;
    if (state.tags.length && !state.tags.every((t) => n.tags.includes(t))) return false; // 多选标签：AND
    if (state.query) {
      const q = state.query.toLowerCase();
      const hay = (n.title + " " + n.content + " " + n.tags.join(" ")).toLowerCase();
      if (!hay.includes(q)) return false;
    }
    return true;
  });
  const order = { pin: (a, b) => (b.pinned - a.pinned) || (b.updatedAt - a.updatedAt),
    time: (a, b) => b.updatedAt - a.updatedAt,
    due: (a, b) => (a.dueAt || "9999") < (b.dueAt || "9999") ? -1 : 1 };
  list.sort(order[state.sort] || order.pin);
  return list;
}

function noteCard(n) {
  const due = dateLabel(n.dueAt);
  const tags = n.tags.map((t) => `<span class="tag">#${escapeHtml(t)}</span>`).join("");
  const check = n.checklist.length
    ? `<div class="note-checklist">${n.checklist.map((c) =>
        `<label class="check-item ${c.done ? "done" : ""}">
           <input type="checkbox" ${c.done ? "checked" : ""} data-act="check" data-idx="${n.checklist.indexOf(c)}" />
           <span>${escapeHtml(c.text)}</span>
         </label>`).join("")}</div>`
    : "";
  const dueHtml = due
    ? `<span class="note-due ${due.cls}">⏰ ${due.text}</span>`
    : `<span class="note-due" style="visibility:hidden">—</span>`;
  const pin = n.pinned ? "on" : "";
  const desk = deskBtn(n);            // v13：贴到桌面 / 从桌面收起（仅启动器文件模式）
  return `
    <article class="note note--${n.color} ${n.done ? "done" : ""}" data-id="${n.id}">
      <div class="note-top">
        ${dueHtml}
        ${n.pinned ? `<span class="note-flag">📌</span>` : ""}
      </div>
      <h3>${escapeHtml(n.title)}</h3>
      ${n.content ? `<p class="body">${escapeHtml(n.content)}</p>` : ""}
      ${tags ? `<div class="note-tags">${tags}</div>` : ""}
      ${check}
      <div class="note-foot">
        <button class="mini-btn ${pin}" data-act="pin" title="置顶">📌</button>
        <button class="mini-btn ${n.done ? "on" : ""}" data-act="done" title="完成">${n.done ? "✓" : "○"}</button>
        <span class="spacer"></span>
        ${desk}
        <button class="mini-btn" data-act="edit" title="编辑">✏️</button>
        ${n.archived
          ? `<button class="mini-btn" data-act="unarchive" title="恢复">↩️</button>
             <button class="mini-btn danger" data-act="del" title="删除">🗑️</button>`
          : `<button class="mini-btn" data-act="archive" title="归档">📦</button>`}
      </div>
      ${desk ? `<div class="desk-hint" data-desk-hint="1"></div>` : ""}
    </article>`;
}

/* ---------- 桌面贴纸桥（v13）按钮 ----------
 * 只在「启动器文件模式」下有这一枚：没有启动器就没有中转站，也就没人能把便签贴到桌面。
 * 归档的便签也给按钮——它就是一张卡，用户随时要能把它从桌面收回来。
 */
function deskBtn(n) {
  if (storeMode !== "file") return "";
  const on = desktopState.placed.has(n.id);
  return `<button class="desk-btn ${on ? "on" : ""}" data-act="desk" title="${on ? "从桌面收起这张便签" : "把这张便签贴到桌面"}">`
    + `<span class="desk-ico">${on ? "📥" : "🖥️"}</span>`
    + `<span class="desk-label">${on ? "从桌面收起" : "贴到桌面"}</span></button>`;
}

/* 手机端（≤680px）：两列「紧密衔接」——按 1→左、2→右、3→左… 交替分列，两列各自
   从上往下排；卡片高度不一也不会在行间留空档，同时**保住"前两张并排"的阅读顺序**。
   为什么不用 CSS column-count：多列是"按列优先"填充，第 2 张会被排到第 1 张下面，
   顺序变成纵向先——那就不是桌面那种观感了。交替分列可以同时拿到顺序与紧凑。
   安全性：所有交互都委托在 #board 上（e.target.closest('.note')），插入列容器不影响。 */
const MASONRY_MQ = "(max-width: 680px)";
let lastCols = 0;
/* v29：列数随屏宽**自动**决定，不再写死 2 列 —— 320px 的老手机到 600px 的大屏
   共用一套代码：可用宽度 / 每张卡的最小可用宽度，取 1..3 列。 */
function masonryCols() {
  const avail = document.documentElement.clientWidth - 28;   // 去掉 .main 左右内边距
  const minCard = 150, gap = 9;
  return Math.max(1, Math.min(3, Math.floor((avail + gap) / (minCard + gap))));
}
function relayoutIfNeeded() {
  const n = window.matchMedia(MASONRY_MQ).matches ? masonryCols() : 0;
  if (n !== lastCols) { lastCols = n; render(); }
}
function boardHtml(list) {
  if (!window.matchMedia(MASONRY_MQ).matches) { lastCols = 0; return list.map(noteCard).join(""); }
  lastCols = masonryCols();
  const n = masonryCols();
  const cols = Array.from({ length: n }, () => []);
  list.forEach((it, i) => cols[i % n].push(noteCard(it)));
  return cols.map((c) => `<div class="board-col">${c.join("")}</div>`).join("");
}
try {
  window.matchMedia(MASONRY_MQ).addEventListener("change", relayoutIfNeeded);
  let rt = 0;
  window.addEventListener("resize", () => { clearTimeout(rt); rt = setTimeout(relayoutIfNeeded, 120); });
  relayoutIfNeeded();
} catch (e) { }
function render() {
  document.querySelectorAll("[data-nav]").forEach((el) => {
    el.classList.toggle("active", el.dataset.nav === state.nav);
  });
  document.querySelectorAll("[data-count]").forEach((el) => {
    const key = el.dataset.count;
    let c = 0;
    if (key === "all") c = notes.filter((n) => !n.archived).length;
    else if (key === "todo") c = notes.filter((n) => !n.archived && !n.done).length;
    else if (key === "done") c = notes.filter((n) => !n.archived && n.done).length;
    else if (key === "archived") c = notes.filter((n) => n.archived).length;
    el.textContent = c;
  });
  const tagSet = new Set();
  notes.forEach((n) => n.tags.forEach((t) => tagSet.add(t)));
  const tagChips = [...tagSet].sort((a, b) => a.localeCompare(b, "zh")).map((t) => {
    const on = state.tags.includes(t);
    const h = tagHue(t);                       // 同一色相：未选中(tint) / 选中(实心深色)
    const style = on
      ? `background:hsl(${h} 72% 38%);border-color:hsl(${h} 72% 38%);color:#fff`
      : `background:hsl(${h} 82% 95%);border-color:hsl(${h} 60% 84%);color:hsl(${h} 60% 30%)`;
    const dot = on ? `background:rgba(255,255,255,.95)` : `background:hsl(${h} 72% 55%)`;
    return `<button class="tag-chip ${on ? "active" : ""}" data-tag="${escapeHtml(t)}"
              aria-pressed="${on}" title="${on ? "点击取消该标签" : "点击累加筛选"}" style="${style}">
              <span class="dot" style="${dot}"></span>${escapeHtml(t)}
            </button>`;
  }).join("");
  const clearBtn = state.tags.length
    ? `<button class="tag-chip tag-clear" data-clear-tags="1" title="清除全部标签筛选">✕ 清除</button>`
    : "";
  $("#tagList").innerHTML = tagChips + clearBtn;
  const mobList = $("#tagListMobile");
  if (mobList) mobList.innerHTML = tagChips + clearBtn;

  const names = { all: "全部便签", todo: "进行中", done: "已完成", archived: "归档" };
  $("#heading").textContent = state.tags.length
    ? `${names[state.nav]} · 标签：${state.tags.map((t) => "#" + t).join(" + ")}`
    : names[state.nav];
  const list = visibleNotes();
  $("#countText").textContent = `${list.length} 条`;
  board.innerHTML = list.length ? boardHtml(list) : `<div class="empty"><div class="big">🗒️</div>这里还没有便签</div>`;

  document.querySelectorAll(".filter-chip").forEach((el) =>
    el.classList.toggle("active", el.dataset.sort === state.sort));
}

/* ---------- 主题 ---------- */
function initTheme() {
  $("#themeBtn").addEventListener("click", () => {
    const root = document.documentElement;
    const next = root.dataset.theme === "dark" ? "light" : "dark";
    root.dataset.theme = next;
    $("#themeBtn").textContent = next === "dark" ? "☀️" : "🌙";
    localStorage.setItem("note-theme", next);
  });
  const saved = localStorage.getItem("note-theme");
  if (saved) {
    document.documentElement.dataset.theme = saved;
    $("#themeBtn").textContent = saved === "dark" ? "☀️" : "🌙";
  }
}

/* ---------- 便签操作（都持久化到 store） ---------- */
function normalize(note) {
  const now = Date.now();
  return {
    id: note.id || newId(), title: note.title || "", content: note.content || "",
    color: note.color || "yellow", done: !!note.done, pinned: !!note.pinned,
    archived: !!note.archived, dueAt: note.dueAt || "", tags: note.tags || [],
    checklist: note.checklist || [], createdAt: note.createdAt || now,
    updatedAt: note.updatedAt || now, syncStatus: note.syncStatus || "local",
  };
}

async function persist(note, changedKeys) {
  const n = normalize(note);
  const i = notes.findIndex((x) => x.id === n.id);
  if (i >= 0) notes[i] = n; else notes.push(n);
  let finalNote = n;
  try {
    // store.save 会「读磁盘 → 只合并本次改动的字段 → 立即落盘」，并返回真正存下去的那一条
    const saved = await store.save(n, changedKeys);
    if (saved) finalNote = normalize(saved);
  } catch (e) {
    console.error("[app] 保存失败", e);
    toast("保存失败：" + (e && e.message ? e.message : e));
    render();
    return;
  }
  const j = notes.findIndex((x) => x.id === finalNote.id);
  if (j >= 0) notes[j] = finalNote; else notes.push(finalNote);
  rememberBaseline(finalNote);
  lastSignature = signature(notes);   // 本地刚写的就是磁盘现状，跟随轮询不必再重绘
  render();
}

/* ---------- 「磁盘快照」基线（S2）----------
 * 界面上的每一条便签都记着它「从磁盘读到时」的样子。每一次落盘只声明真正改动的字段，
 * 由 store 合并进磁盘版；落盘后再把基线刷新成磁盘上的最新形态。
 * 这样：贴纸端刚写进去的勾选，绝不会被网页版内存里的旧整条盖掉。
 */
let notesBaseline = new Map();   // id -> 归一化后的 JSON 字符串

function rememberBaseline(n) {
  try { notesBaseline.set(n.id, JSON.stringify(normalize(n))); } catch (e) { /* 忽略 */ }
}

function setBaseline(list) {
  notesBaseline = new Map();
  for (const n of list) rememberBaseline(n);
}

/* 稳定的「内容指纹」：只含会被别处改动的字段，用来判断磁盘上是否真的变了 */
function signature(list) {
  return JSON.stringify(list.map((n) => [
    n.id, n.updatedAt, n.title, n.content, n.color, n.done, n.pinned, n.archived, n.dueAt,
    (n.tags || []).join("\u0001"),
    (n.checklist || []).map((c) => c.text + "\u0002" + (c.done ? 1 : 0)).join("\u0001"),
  ]));
}

async function toggleCheck(note, idx) {
  note.checklist[idx].done = !note.checklist[idx].done;
  note.updatedAt = Date.now();
  // 只声明「清单第 idx 项的 done + updatedAt」：磁盘上别的东西（包括贴纸端刚写的）一律保留
  await persist(note, ["checklistDone:" + idx, "updatedAt"]);
}

/* ---------- 桌面贴纸桥（v13）----------
 * 为什么这么绕：浏览器不能自己创建原生桌面窗口；桌面贴纸程序有一条硬约束「自身不监听端口」
 * （自测断言还禁用了 Socket/TcpListener/HttpListener/NamedPipe 符号），端口与管道都不可用
 * ⇒ 只能经启动器「文件中转」：
 *   POST /api/desktop {action:"place"|"remove", noteId}  → 启动器写一条请求（唯一写者 = 启动器）
 *   贴纸程序最多 1 秒后消费 ⇒ 贴出 / 收起那张卡
 *   GET  /api/desktop → 返回「已贴出清单」（唯一写者 = 贴纸程序）⇒ 按钮状态一律以它为准，
 *                        所以在贴纸上点 ❌ 关掉那张卡，这里最多 3 秒就会变回「贴到桌面」。
 */
const desktopState = {
  placed: new Set(), lastRequestSeq: 0, ackSeq: 0, savedAtMs: 0,
  reachable: false, pendingNoteId: null, pendingAction: null, pendingSince: 0,
  // v19：由启动器上报的真实状态（"贴纸到底在不在跑""还有几条请求没被处理"）。
  // 旧版是让网页自己拿 seq 差去猜，并在猜不出来时叫用户去双击一个手册文件——那条提示已删除。
  stickerRunning: false, pendingRequests: 0,
};

function deskWaitingMs() {
  return desktopState.pendingSince ? Date.now() - desktopState.pendingSince : 0;
}

async function pollDesktop() {
  if (storeMode !== "file") return;
  try {
    const r = await fetch("/api/desktop", { cache: "no-store" });
    if (!r.ok) throw new Error("HTTP " + r.status);
    const j = await r.json();
    desktopState.reachable = true;
    const real = new Set((j.placed || []).map((p) => p.noteId));
    const waiting = (j.lastRequestSeq || 0) > (j.ackSeq || 0);
    // 自己刚发出、贴纸端还没消费的那一条：先按目标态显示（真实状态一旦确认就以它为准）
    if (waiting && desktopState.pendingNoteId) {
      if (desktopState.pendingAction === "place") real.add(desktopState.pendingNoteId);
      else real.delete(desktopState.pendingNoteId);
    }
    desktopState.placed = real;
    desktopState.lastRequestSeq = j.lastRequestSeq || 0;
    desktopState.ackSeq = j.ackSeq || 0;
    desktopState.savedAtMs = j.placedSavedAtMs || 0;
    desktopState.stickerRunning = !!j.stickerRunning;                    // v19
    desktopState.pendingRequests = j.pendingRequests || 0;               // v19
    if (!waiting) { desktopState.pendingNoteId = null; desktopState.pendingAction = null; desktopState.pendingSince = 0; }
    else if (!desktopState.pendingSince) desktopState.pendingSince = Date.now();
  } catch (e) {
    desktopState.reachable = false;      // 启动器没在跑 / 版本太旧：按钮保持"最后已知"，并给提示
  }
  refreshDesktop();
}

/* 只改按钮与那行小字，不重绘整个面板（重绘会打断滚动与输入） */
function refreshDesktop() {
  document.querySelectorAll(".note").forEach((card) => {
    const id = card.dataset.id;
    const btn = card.querySelector('[data-act="desk"]');
    if (btn) {
      const on = desktopState.placed.has(id);
      const ico = btn.querySelector(".desk-ico");
      const lab = btn.querySelector(".desk-label");
      if (ico) ico.textContent = on ? "📥" : "🖥️";
      if (lab) lab.textContent = on ? "从桌面收起" : "贴到桌面";
      btn.classList.toggle("on", on);
      btn.title = on ? "从桌面收起这张便签" : "把这张便签贴到桌面";
    }
    const hint = card.querySelector("[data-desk-hint]");
    if (hint) {
      const waiting = deskWaitingMs() > 5000 && desktopState.pendingNoteId === id;
      // v19：不再让用户去找/双击任何文件——启动器会自己把贴纸拉起来；这里只说清"现在到哪一步了"
      hint.textContent = waiting
        ? (desktopState.stickerRunning ? "贴纸程序正在处理…" : "贴纸程序未在运行，正在自动启动…")
        : "";
    }
  });

  // 全局提示（v13 实测补）：逐卡那行小字在「刷新页面后」会丢（pendingNoteId 归零），
  // 于是"点了没反应"变成无从判断。v19 起以启动器上报的真实状态（stickerRunning / pendingRequests）
  // 说话，话术里不再出现任何"请手动双击某个 exe"。
  const host = $("#countText");
  if (host && !$("#deskWait")) {
    const w = document.createElement("span");
    w.id = "deskWait";
    w.className = "desk-wait";
    host.parentNode.insertBefore(w, host.nextSibling);
  }
  const waitEl = $("#deskWait");
  if (waitEl) {
    const pending = desktopState.pendingRequests || 0;
    if (!desktopState.reachable) {
      waitEl.textContent = "启动器没在运行 —— 请启动「贴贴便签」再刷新本页（桌面贴纸由它转发请求）";
    } else if (pending > 0 && deskWaitingMs() > 4000) {
      waitEl.textContent = desktopState.stickerRunning
        ? "贴纸程序正在处理 " + pending + " 条请求…"
        : "贴纸程序未在运行，已自动尝试启动（还有 " + pending + " 条请求待处理）";
    } else {
      waitEl.textContent = "";
    }
  }
}

async function deskAct(note) {
  const action = desktopState.placed.has(note.id) ? "remove" : "place";
  try {
    const r = await fetch("/api/desktop", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ action, noteId: note.id }),
    });
    const j = await r.json().catch(() => ({}));
    if (!r.ok || !j.ok) {
      // 最常见的失败：浏览器里的页面已经是新版，但正在跑的还是旧启动器（没有 /api/desktop）
      const old = (r.status === 404 || r.status === 405 || !j.error);
      toast("贴到桌面失败：" + (j.error || ("HTTP " + r.status))
        + (old ? "（请重启启动器：关掉「贴贴便签」窗口，再双击它）" : ""));
      return;
    }
    desktopState.pendingNoteId = note.id;
    desktopState.pendingAction = action;
    desktopState.pendingSince = Date.now();
    if (j.seq) desktopState.lastRequestSeq = j.seq;
    // 乐观反馈：立刻切到目标态，下一次轮询以真实状态为准
    if (action === "place") desktopState.placed.add(note.id); else desktopState.placed.delete(note.id);
    // v13.1：启动器会顺手把贴纸程序拉起来（如果没在跑），这里按它的回执说清结果。
    // v19：回执里已经不再有"找不到贴纸程序文件"这一类失败（贴纸就是本 exe 的另一种模式），
    // 而且不再给"请手动双击某个文件"的指示——真失败了只指向启动器窗口里的日志。
    const launchMsg = {
      started: "已自动启动贴纸程序，1–2 秒后贴上",
      starting: "贴纸程序正在启动…",
      running: "贴纸程序已在运行",
      disabled: "已跳过自动启动（自动化开关）",
      notfound: "自动启动失败：取不到本程序自身路径",
      failed: "自动启动贴纸程序失败",
    }[j.sticker];
    const failHint = (j.sticker === "failed" || j.sticker === "notfound")
      ? "（详见「贴贴便签」窗口里的日志）" : "";
    toast((launchMsg || (action === "place" ? "已请求贴到桌面" : "已请求从桌面收起")) + failHint);
    refreshDesktop();
    setTimeout(pollDesktop, 900);
  } catch (e) {
    toast("贴到桌面失败：" + (e && e.message ? e.message : e));
  }
}

board.addEventListener("click", async (e) => {
  const actBtn = e.target.closest("[data-act]");
  const card = e.target.closest(".note");
  if (!card) return;
  const note = notes.find((n) => n.id === card.dataset.id);
  if (!note) return;

  if (e.target.closest('[data-act="check"]') || e.target.closest(".check-item")) return;

  if (actBtn) {
    e.stopPropagation();
    const act = actBtn.dataset.act;
    let changed = null;
    // 桌面桥：不碰 notes.json（一个字节都不写），只让启动器转一条请求给贴纸程序
    if (act === "desk") { await deskAct(note); return; }
    if (act === "pin") { note.pinned = !note.pinned; changed = ["pinned"]; }
    else if (act === "done") { note.done = !note.done; changed = ["done"]; }
    else if (act === "archive") { note.archived = true; changed = ["archived"]; }
    else if (act === "unarchive") { note.archived = false; changed = ["archived"]; }
    else if (act === "del") {
      notes = notes.filter((n) => n.id !== note.id);
      await store.remove(note.id);
      notesBaseline.delete(note.id);
      lastSignature = signature(notes);
      toast("已删除");
      render();
      return;
    } else if (act === "edit") { openModal(note); return; }
    if (!changed) return;
    note.updatedAt = Date.now();
    changed.push("updatedAt");
    await persist(note, changed);
    return;
  }
  openModal(note);
});

board.addEventListener("change", (e) => {
  const cb = e.target.closest('input[type="checkbox"]');
  if (!cb || cb.dataset.act !== "check") return;
  const card = e.target.closest(".note");
  const note = notes.find((n) => n.id === card.dataset.id);
  if (!note) return;
  toggleCheck(note, Number(cb.dataset.idx));
});

/* ---------- 搜索 / 筛选 ---------- */
$("#searchInput").addEventListener("input", (e) => { state.query = e.target.value; render(); });
document.querySelectorAll(".filter-chip").forEach((el) =>
  el.addEventListener("click", () => { state.sort = el.dataset.sort; render(); }));
document.querySelectorAll("[data-nav]").forEach((el) =>
  el.addEventListener("click", () => { state.nav = el.dataset.nav; state.tags = []; render(); }));
/* 标签点击：委托到 document —— 桌面侧边栏的 #tagList 与手机抽屉的 #tagListMobile
   共用同一段逻辑（v28：手机上多了一个入口，不需要复制一份处理代码）。 */
document.addEventListener("click", (e) => {
  const clear = e.target.closest("[data-clear-tags]");
  if (clear) { state.tags = []; render(); return; }
  const t = e.target.closest("[data-tag]");
  if (!t) return;
  const name = t.dataset.tag;
  state.tags = state.tags.includes(name)
    ? state.tags.filter((x) => x !== name)   // 再点一次 = 取消该标签
    : [...state.tags, name];                 // 累加多选
  render();
});
$("#newBtn").addEventListener("click", () => openModal(null));
$("#newBtnMobile").addEventListener("click", () => openModal(null));

/* ---------- 手机：☰ = 按标签筛选抽屉 ---------- */
(function initTagDrawer() {
  const btn = $("#menuBtn"), drawer = $("#tagDrawer"), mask = $("#drawerMask"), done = $("#drawerDone");
  if (!btn || !drawer) return;
  const setOpen = (v) => {
    drawer.classList.toggle("open", v);
    if (mask) mask.classList.toggle("open", v);
  };
  btn.addEventListener("click", () => setOpen(!drawer.classList.contains("open")));
  if (mask) mask.addEventListener("click", () => setOpen(false));
  if (done) done.addEventListener("click", () => setOpen(false));
})();

/* ---------- 导出 / 导入 ---------- */
function exportJSON() {
  const blob = new Blob([JSON.stringify(notes, null, 2)], { type: "application/json" });
  const a = document.createElement("a");
  a.href = URL.createObjectURL(blob);
  a.download = `tietie-backup-${new Date().toISOString().slice(0, 10)}.json`;
  document.body.appendChild(a);
  a.click();
  document.body.removeChild(a);
  URL.revokeObjectURL(a.href);
  toast("已导出 JSON");
}

function importJSON(file) {
  const reader = new FileReader();
  reader.onload = async () => {
    try {
      const arr = JSON.parse(reader.result);
      if (!Array.isArray(arr)) throw new Error("文件格式不正确（应为数组）");
      const valid = arr.filter((n) => n && typeof n === "object").map((n) => normalize(n));
      await store.saveMany(valid);
      notes = await store.getAll();
      render();
      toast(`已导入 ${arr.length} 条`);
    } catch (err) {
      toast("导入失败：" + err.message);
    }
  };
  reader.onerror = () => toast("读取文件失败");
  reader.readAsText(file);
}

$("#exportBtn").addEventListener("click", exportJSON);
$("#importBtn").addEventListener("click", () => $("#importFile").click());
$("#importFile").addEventListener("change", (e) => {
  const f = e.target.files[0];
  if (f) importJSON(f);
  e.target.value = "";
});

/* ---------- 弹窗 ---------- */
function renderColorPicker(sel) {
  $("#colorPicker").innerHTML = COLORS.map((c) =>
    `<span class="color-swatch ${c.key === sel ? "sel" : ""}" data-color="${c.key}" style="background:${c.hex}"></span>`).join("");
}
let pickedColor = "yellow";

$("#colorPicker").addEventListener("click", (e) => {
  const sw = e.target.closest("[data-color]");
  if (!sw) return;
  pickedColor = sw.dataset.color;
  renderColorPicker(pickedColor);
});

function openModal(note) {
  editingId = note ? note.id : null;
  $("#modalTitle").textContent = note ? "编辑便签" : "新建便签";
  $("#fTitle").value = note ? note.title : "";
  $("#fContent").value = note ? note.content : "";
  $("#fDue").value = note ? note.dueAt : "";
  $("#fTags").value = note ? note.tags.join(", ") : "";
  $("#fChecklist").value = note ? note.checklist.map((c) => c.text).join("\n") : "";
  pickedColor = note ? note.color : "yellow";
  renderColorPicker(pickedColor);
  $("#modalMask").classList.add("open");
  $("#fTitle").focus();
}
function closeModal() { $("#modalMask").classList.remove("open"); }

$("#cancelBtn").addEventListener("click", closeModal);
$("#modalMask").addEventListener("click", (e) => { if (e.target === e.currentTarget) closeModal(); });

$("#saveBtn").addEventListener("click", async () => {
  const title = $("#fTitle").value.trim() || "无标题";
  const tags = $("#fTags").value.split(/[,，]/).map((s) => s.trim()).filter(Boolean);
  const dueAt = $("#fDue").value;

  // S2 修复：保留原有勾选状态（按文字匹配，同名的按先后顺序一一对应）。
  // 旧版这里把每一项都硬写成 done:false —— 用户刚在桌面贴纸上勾好的，一编辑保存就全没了。
  const existing = editingId ? (((notes.find((n) => n.id === editingId) || {}).checklist) || []) : [];
  const usedPrev = new Set();
  const checklist = $("#fChecklist").value.split("\n").map((s) => s.trim()).filter(Boolean)
    .map((text) => {
      let done = false;
      for (let i = 0; i < existing.length; i++) {
        if (!usedPrev.has(i) && existing[i].text === text) { usedPrev.add(i); done = !!existing[i].done; break; }
      }
      return { text, done };
    });

  if (editingId) {
    const note = notes.find((n) => n.id === editingId);
    if (note) {
      note.title = title; note.content = $("#fContent").value.trim(); note.color = pickedColor;
      note.tags = tags; note.dueAt = dueAt; note.checklist = checklist; note.updatedAt = Date.now();
      // 只声明这些字段：磁盘上没被这里改到的内容（例如贴纸端写的勾选）保持不动
      await persist(note, ["title", "content", "color", "tags", "dueAt", "checklist", "updatedAt"]);
    }
    toast("已保存");
  } else {
    const note = {
      id: newId(), color: pickedColor, title, done: false, pinned: false, archived: false,
      dueAt, tags, content: $("#fContent").value.trim(), checklist,
      createdAt: Date.now(), updatedAt: Date.now(),
    };
    await persist(note, null);   // 新便签：整条写入
    toast("已创建");
  }
  closeModal();
});

/* ---------- toast ---------- */
let toastTimer;
function toast(msg) {
  const t = $("#toast");
  t.textContent = msg;
  t.classList.add("show");
  clearTimeout(toastTimer);
  toastTimer = setTimeout(() => t.classList.remove("show"), 1800);
}

/* ---------- 启动 ---------- */
async function boot() {
  initTheme();
  const opened = await openStore();   // exe 环境 → 文件；否则回退 IndexedDB
  store = opened.store;
  storeMode = opened.mode;

  if (storeMode === "file") {
    const moved = await migrateLegacyToFile(store);   // 首启把浏览器里的旧便签搬进数据文件
    if (moved > 0) toast(`已把浏览器里的 ${moved} 条便签搬进数据文件`);
  }

  notes = await store.getAll();
  setBaseline(notes);   // 记下「从磁盘读到的样子」，后面每次落盘只提交真正改动的字段
  // v22（用户 2026-09-22 报「安装包安装后,会有测试用的便签残余」）：这里原本会在“库为空”时写入 10 条
  // 示例便签（当时写死了 10 条）。已整块删除：空库就是空库（页面上本来就有「🗒️ 这里还没有便签」的空状态），
  // 示例数据只会变成用户装完要自己收拾的垃圾。
  await pollDesktop();  // 先问一次桌面现状，免得按钮先画出「贴到桌面」再跳成「从桌面收起」
  render();
  startFileWatch();     // 文件模式下跟着磁盘走：贴纸端勾上的，页面上也会跟着变
}

/* ---------- 文件模式的轻量跟随（S2）----------
 * 桌面贴纸端会直接改 data/notes.json。网页版没有推送通道，所以这里每 3 秒比对一次内容指纹：
 * 只有「磁盘真的变了 / 页面可见 / 没在编辑」时才重载重绘，绝不打断正在输入的人。
 */
let lastSignature = "";
function startFileWatch() {
  if (storeMode !== "file") return;
  lastSignature = signature(notes);
  setInterval(async () => {
    if (document.hidden) return;
    if ($("#modalMask").classList.contains("open")) return;
    await pollDesktop();          // v13：贴纸上的 ❌ / 新贴出的卡，最多 3 秒后反映到按钮上
    try {
      const fresh = await store.getAll();
      if (signature(fresh) === lastSignature) return;
      notes = fresh.map((n) => normalize(n));
      setBaseline(notes);
      lastSignature = signature(notes);
      render();
      console.log("[app] 磁盘内容已变化（可能是桌面贴纸端写入），界面已同步");
    } catch (e) { /* 单次读取失败不打扰用户 */ }
  }, 3000);
}
boot();
console.log(
  "%c贴贴便签 电脑版已加载",
  "color:#ff7a9e;font-weight:bold",
  storeMode === "file" ? "数据模式：本地文件 data/notes.json（由启动器管理）" : "数据模式：浏览器 IndexedDB"
);