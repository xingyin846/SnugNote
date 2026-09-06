/* ===== 贴贴便签 · 电脑版（IndexedDB 持久化 + JSON 导出/导入） ===== */

const COLORS = [
  { key: "yellow", hex: "#fff6c9" },
  { key: "pink",   hex: "#ffe3ee" },
  { key: "blue",   hex: "#dcefff" },
  { key: "green",  hex: "#e3f6d0" },
  { key: "purple", hex: "#ece3ff" },
  { key: "orange", hex: "#ffe8d3" },
];

/* ---------- 首次种子数据（仅当库为空且从未 seed 过时写入） ---------- */
const SEED = [
  { id: "seed-1", color: "yellow", title: "周末采购清单", done: false, pinned: false, archived: false, dueAt: "", tags: ["生活"], content: "",
    checklist: [
      { text: "鲜牛奶 2 盒", done: true }, { text: "鸡蛋 30 个", done: false },
      { text: "咖啡豆", done: false }, { text: "燕麦片", done: true },
    ] },
  { id: "seed-2", color: "pink", title: "健身打卡", done: false, pinned: false, archived: false, dueAt: nextDay(1), tags: ["健康", "习惯"], content: "",
    checklist: [{ text: "晨跑 30 分钟", done: false }, { text: "平板支撑 3 组", done: false }] },
  { id: "seed-3", color: "blue", title: "读书笔记：原子习惯", done: false, pinned: true, archived: false, dueAt: "", tags: ["读书"], content: "每天进步 1%，一年后你会强 37 倍。习惯不是目标，而是系统。\n关键：让好习惯显而易见、有吸引力、简单易行、令人愉悦。", checklist: [] },
  { id: "seed-4", color: "green", title: "项目周报（今天提交）", done: false, pinned: false, archived: false, dueAt: today(), tags: ["工作", "重要"], content: "整理本周进度 + 下周计划，下班前发给 Leader。", checklist: [
    { text: "汇总已完成事项", done: true }, { text: "写本周风险", done: false }, { text: "排下周规划", done: false } ] },
  { id: "seed-5", color: "purple", title: "灵感闪现", done: false, pinned: false, archived: false, dueAt: "", tags: ["灵感"], content: "做一个「便签 + 白板」的瀑布流排版，卡片支持拖拽拼图更好玩…", checklist: [] },
  { id: "seed-6", color: "orange", title: "给妈妈打电话", done: true, pinned: false, archived: false, dueAt: nextDay(-1), tags: ["生活"], content: "提醒爸妈体检报告记得去取。", checklist: [] },
  { id: "seed-7", color: "yellow", title: "旅行清单", done: false, pinned: true, archived: false, dueAt: nextDay(7), tags: ["旅行"], content: "国庆周边游，提前订民宿和门票。", checklist: [
    { text: "订民宿", done: true }, { text: "买门票", done: false }, { text: "准备相机", done: false } ] },
  { id: "seed-8", color: "blue", title: "代码重构 TODO", done: false, pinned: false, archived: false, dueAt: nextDay(3), tags: ["工作", "开发"], content: "整理 demo 的响应式断点，手机端侧栏收进底部导航。", checklist: [
    { text: "抽离颜色变量", done: true }, { text: "统一间距", done: false } ] },
  { id: "seed-9", color: "pink", title: "朋友生日", done: false, pinned: false, archived: true, dueAt: nextDay(15), tags: ["生活"], content: "下周三，记得订蛋糕。", checklist: [] },
  { id: "seed-10", color: "green", title: "收藏的好文", done: true, pinned: false, archived: false, dueAt: "", tags: ["读书", "灵感"], content: "《如何用 3 秒进入心流》—— 已读完，值得回看。", checklist: [] },
];
const SEED_FLAG = "tietie-seeded";

/* ---------- 状态 ---------- */
let state = { nav: "all", sort: "pin", query: "", tag: null };
let editingId = null; // null = 新建
let notes = [];       // 从 store 加载
let store = new IndexedDBAdapter();

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

/* ---------- 渲染 ---------- */
function visibleNotes() {
  let list = notes.filter((n) => {
    if (n.archived !== (state.nav === "archived")) return false;
    if (state.nav === "todo" && n.done) return false;
    if (state.nav === "done" && !n.done) return false;
    if (state.tag && !n.tags.includes(state.tag)) return false;
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
        <button class="mini-btn" data-act="edit" title="编辑">✏️</button>
        ${n.archived
          ? `<button class="mini-btn" data-act="unarchive" title="恢复">↩️</button>
             <button class="mini-btn danger" data-act="del" title="删除">🗑️</button>`
          : `<button class="mini-btn" data-act="archive" title="归档">📦</button>`}
      </div>
    </article>`;
}

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
  $("#tagList").innerHTML = [...tagSet].map((t) =>
    `<button class="tag-chip ${state.tag === t ? "active" : ""}" data-tag="${escapeHtml(t)}">
       <span class="dot" style="background:var(--note-edge)"></span>${escapeHtml(t)}
     </button>`).join("");

  const names = { all: "全部便签", todo: "进行中", done: "已完成", archived: "归档" };
  $("#heading").textContent = names[state.nav];
  const list = visibleNotes();
  $("#countText").textContent = `${list.length} 条`;
  board.innerHTML = list.length
    ? list.map(noteCard).join("")
    : `<div class="empty"><div class="big">🗒️</div>这里还没有便签</div>`;

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

async function persist(note) {
  const n = normalize(note);
  const i = notes.findIndex((x) => x.id === n.id);
  if (i >= 0) notes[i] = n; else notes.push(n);
  await store.save(n);
  render();
}

async function toggleCheck(note, idx) {
  note.checklist[idx].done = !note.checklist[idx].done;
  await persist(note);
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
    if (act === "pin") note.pinned = !note.pinned;
    else if (act === "done") note.done = !note.done;
    else if (act === "archive") note.archived = true;
    else if (act === "unarchive") note.archived = false;
    else if (act === "del") {
      notes = notes.filter((n) => n.id !== note.id);
      await store.remove(note.id);
      toast("已删除");
      render();
      return;
    } else if (act === "edit") { openModal(note); return; }
    await persist(note);
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
  el.addEventListener("click", () => { state.nav = el.dataset.nav; state.tag = null; render(); }));
$("#tagList").addEventListener("click", (e) => {
  const t = e.target.closest("[data-tag]");
  if (!t) return;
  state.tag = state.tag === t.dataset.tag ? null : t.dataset.tag;
  render();
});
$("#newBtn").addEventListener("click", () => openModal(null));
$("#newBtnMobile").addEventListener("click", () => openModal(null));

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
      for (const n of arr) {
        if (!n || typeof n !== "object") continue;
        await store.save(normalize(n));
      }
      notes = await store.getAll();
      localStorage.setItem(SEED_FLAG, "1");
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
  const checklist = $("#fChecklist").value.split("\n").map((s) => s.trim()).filter(Boolean)
    .map((text) => ({ text, done: false }));
  const tags = $("#fTags").value.split(/[,，]/).map((s) => s.trim()).filter(Boolean);
  const dueAt = $("#fDue").value;

  if (editingId) {
    const note = notes.find((n) => n.id === editingId);
    if (note) {
      note.title = title; note.content = $("#fContent").value.trim(); note.color = pickedColor;
      note.tags = tags; note.dueAt = dueAt; note.checklist = checklist; note.updatedAt = Date.now();
      await persist(note);
    }
    toast("已保存");
  } else {
    const note = {
      id: newId(), color: pickedColor, title, done: false, pinned: false, archived: false,
      dueAt, tags, content: $("#fContent").value.trim(), checklist,
      createdAt: Date.now(), updatedAt: Date.now(),
    };
    await persist(note);
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
  await store.init();
  notes = await store.getAll();
  if (!localStorage.getItem(SEED_FLAG) && notes.length === 0) {
    for (const s of SEED) {
      const n = normalize(s);
      notes.push(n);
      await store.save(n);
    }
    localStorage.setItem(SEED_FLAG, "1");
  }
  render();
}
boot();
console.log("%c贴贴便签 电脑版已加载（IndexedDB 持久化）", "color:#ff7a9e;font-weight:bold");
