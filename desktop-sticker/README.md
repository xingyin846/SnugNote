# S1 便签贴纸（desktop-sticker）交付说明

> 任务：`t2` 实现 S1 贴纸程序　|　产出人：工程师
> 规格依据：**依据《S1 原生便签卡视觉与交互规格》 + grep 定位串**（`^## 15\. 决策记录`、`^### 15\.1 六条范围决策`、`^### 15\.2 两条硬约束`、`^### 15\.3 `、`^### 4\.4 内容自然高度`、`^## 4\.3 纵向流式排版公式`）
> **按规格 §15.3 规则 1/2/4：本文不内嵌规格文件的哈希、字节数或行数**（规则 2 明令禁止硬编码；规则 4 说明内嵌自身哈希会自指失真）。锚点一律"**实测哈希 + 实测时间**"或 grep 定位串。
> 实现期间规格连续演进（v2→v9，最后一次实测 mtime `2026-09-20 20:21:30`），我全程以**磁盘现状**为准；v9 把 §4.4 的 `ContentEnd` 消歧为"**§4.3 中加底部行那一行之前的 `y`**"并给出对账数字 `250 / 224 / 145`，与本实现完全一致。

---

## 增量 1（v13，2026-09-21）：网页「贴到桌面」按钮 + 托盘「已贴出的便签」

> **权威规格**：`.dsh-meow/sticker/14-增量规格v13-贴到桌面与托盘清单.md`（本节判据一律以它为准）。
> **一句话**：网页版每张卡片多一枚「**贴到桌面 / 从桌面收起**」；贴纸程序**第一次有系统托盘**（「已贴出的便签（N）」+ 每张一行 + 退出贴纸）。
> **为什么必须绕一圈**：浏览器不能自建原生桌面窗口；贴纸侧硬约束「自身不监听端口」且自测禁用 Socket/TcpListener/HttpListener/**NamedPipe** 符号 ⇒ 端口与管道都不可用，只能**文件中转**（经启动器）。

### 增量 1.1 协议与单写者矩阵

| 文件 | 唯一写者 | 读者 | 作用 |
|---|---|---|---|
| `data\bridge\request.json` | **启动器** | 贴纸（只读） | 请求队列（只增不删，带单调 `seq`） |
| `data\bridge\placed.json` | **贴纸** | 启动器（只读） | 已贴出清单的只读视图 + `ackSeq` |

- 启动器新增 `GET /api/desktop`（**只返回解析后的字段**，缺失/损坏 ⇒ 空数组 + `ok:false`，**绝不 500**）与
  `POST /api/desktop`（校验 `action∈{place,remove}` 且 `noteId` 存在于 `notes.json`，否则 400 且**不写任何文件**）。
- 贴纸用 WinForms `Timer` **每 1 s 轮询**消费（刻意不用 `FileSystemWatcher`，少一类竞态），**启动时也读一次**
  ⇒ 贴纸没开时点的按钮，等它下次启动**自动补上**（请求持久化，不丢）。
- 桥文件放 `data\bridge\` 子目录 ⇒ `data\` 顶层**仍只有** `notes.json` 与 `notes.json.bak`。
- **本批对 `notes.json` 零写入**（一个字节都不写）。

### 增量 1.2 新增/改动的源码

| 文件 | 改动 |
|---|---|
| `src/DesktopBridge.cs` | **新增**。`BridgeProtocol`（路径/解析/视图序列化，纯函数）+ `DesktopBridge`（轮询消费、ack 推进、视图发布）。`request.json` 只读、`placed.json` 只写；原子替换（`temp + File.Replace`，绝不 Delete+Move） |
| `src/TrayMenu.cs` | **新增**。`TrayMenu.BuildRows/Create`（行模型 + 真菜单，可无窗口驱动）+ `TrayIcon`（`NotifyIcon`，图标**运行时自绘** `Geometry.DrawPin` → `GetHicon` → `Clone` → `DestroyIcon`） |
| `src/StickerManager.cs` | 新增 `ApplyBridgeRequest(place, noteId)`（place 幂等 / remove 走既有 X 路径）、`RaiseCard`、`BridgePlacements/TitleOf/PlacedNoteIdsOrdered`、`SetBridgeAck`；`Persist()` 末尾回调 `_bridgePublisher`（视图与 state 同源同步）；`CardCount` 在 **headless** 下取放置集合（自测不建窗，见 §增量 1.5） |
| `src/Program.cs` | 线路装配：`bridgeDir = <notes.json 所在目录>\bridge` → 建 `DesktopBridge` + `TrayIcon` → `AttachBridgePublisher` → `mgr.Start()` → **`bridge.Start()`（先消费再判空集退出）**；`ApplicationExit` 里停止轮询并 `Dispose` 托盘 |
| `src/UiStrings.cs` | +5 条托盘文案（仍按纯 ASCII 六进制转义书写） |
| `src/SelfTest.cs` | **+8 条** v13 断言（B1–B8，沙盒夹具/无窗口）；顺手修掉一条**拿用户数据当夹具**的老断言（§增量 1.5） |
| `launcher/Program.cs` | **冻结至今首次被改**（+1 个 `DesktopBridge` 小类 + 一个极简 JSON 解析器 `BNode`）。新增两个路由；**`/api/notes` 与静态服务语义一行未改**；新增 `--no-open`（自动化验证用，默认行为不变） |
| `demo/app.js` | 卡片脚部新增 `.desk-btn`（贴到桌面/从桌面收起）+ `pollDesktop/refreshDesktop/deskAct`；`boot` 先问一次桌面现状；既有的 3 s 跟随轮询里一并轮询桌面状态 |
| `demo/styles.css` | `.desk-btn` / `.desk-hint` 样式（窄屏退化成图标） |

### 增量 1.3 复现命令（v13 新增）

```powershell
# 无窗口自测（v13 新增 8 条；期望 PASS=87 FAIL=0）
powershell -NoProfile -ExecutionPolicy Bypass -File .tools\capture-run.ps1 `
  -Exe .tools\sticker_build\Sticker.exe -ArgList '--selftest' `
  -OutTxt .tools\sticker_build\evidence\v13-selftest-raw-output.txt `
  -OutBin .tools\sticker_build\evidence\v13-selftest-stdout.bin `
  -ErrBin .tools\sticker_build\evidence\v13-selftest-stderr.bin

# 启动器接口回归（沙盒副本；既有 /api/notes 契约 + 新增 /api/desktop；期望 35/35）
powershell -NoProfile -ExecutionPolicy Bypass -File .tools\v13-launcher-api.ps1

# 桥端到端：真启动器 + 真贴纸 + 真窗口，全在沙盒里（期望 14/14；自证用户数据三元组不变）
powershell -NoProfile -ExecutionPolicy Bypass -File .tools\bridge-e2e.ps1

# 作者自证变异：4 条变异各按其目标断言变红、2 条控制全绿（期望 8/8；变异目录用完即删）
powershell -NoProfile -ExecutionPolicy Bypass -File .tools\_v13-mutation-check.ps1
```

### 增量 1.4 本批产物身份（实测，写于本节时）

| 项 | 实测值 |
|---|---|
| `Sticker.exe` | **151552 B** / SHA-256 **`4F89E96B0FEE0FC7AA6D349342B9A83EE50EA09D3F1C12E2DCF9363B468557FB`** / mtime **2026-09-21 11:27:46.448**（**以运行输出为准**；exe 哈希不可复现。本批构建过两次：首建 `0FB1C7C1…/11:19:06.656` 之后为修掉"自测崩溃不报错"又重建，见 §增量 1.5 第 3 条） |
| manifest 源条数 / 漂移 | **16 / 漂移 0**（v12 为 14；+`DesktopBridge.cs` +`TrayMenu.cs`） |
| `--selftest` | **`PASS=87 FAIL=0`**（v12 为 79；+8 条 v13） |
| `.tools\v13-launcher-api.ps1` | **TOTAL=35 PASS=35 FAIL=0** |
| `.tools\bridge-e2e.ps1` | **TOTAL=14 PASS=14 FAIL=0** |
| `.tools\_v13-mutation-check.ps1`（新） | **TOTAL=8 PASS=8 FAIL=0**（4 条变异各按其目标断言变红 + 2 条控制全绿 + 变异目录已删 + 交付 exe 未动） |
| **独立验证（v13）** | **verdict = pass**（报告 `.dsh-meow/sticker/15-独立验证-v13贴到桌面.md`，2026-09-21 11:40:57.817 / 29737 B）：验证者自写 4 条**接线级**变异、独立重算 manifest 16/16 源摘要、独立复现全部判据；F1(medium，用户的重启动作)+F2/F3/F4 见规格 §10.3 |
| 既有回归 | `s2-write-e2e` **12/12**、`s2-merge-test` **9/9**、`s2-pinkdot-check` **9/9**、`interaction-test` **TOTAL=20 FAIL=0**（均在**本代最终 exe** 上重跑） |

### 增量 1.5 两条必须知道的实现口径

1. **自测里的「无窗口放置」**：`StickerManager` 新增第 7 个构造参数 `headless`，**只有 `SelfTest` 传 true**。
   headless 下 `CreateCard` 只登记"放置集合"（`_order`）而不建窗，`CardCount` 随之改取 `_order.Count`
   ——产品路径（`headless=false`）**仍是原来的 `_forms.Count`，定义一字未改**。真正的"建出真窗口"由
   `.tools\bridge-e2e.ps1` 端到端证明（它断言窗口数与窗口标题）。
2. **顺手修掉的四个既有缺陷**（都在 v13 §7 有记录）：①启动器对带 body 的非支持方法**不排空请求体**就回 405，
   客户端还在写 ⇒ 报传输错误而不是读到 405（现改为**先排空再回**）；②`spec-4.4-notes-expected-table`
   原本硬编码 `199/185/153` 并**拿用户的 `data\notes.json` 当夹具** ⇒ 用户 2026-09-21 11:03 给自己那条便签加了内容，
   153 变 185、断言变红（**用改动前的源码 A/B 复现过：与本批无关**）⇒ 现改为断言**内置 S1 快照夹具**，
   活体文件只留下"只读/可解析/两个禁止值"三条与数据无关的检查；③新脚手架把 `"_menu.Items.Add("` 字面量写在
   `SelfTest.cs` 里，跨文件扫描时把自己数了进去 ⇒ 扫描改为**跳过 `SelfTest.cs`**；④`tray-menu-lists-placed` **盲目按下标取菜单项**，托盘行数一变少就抛 `ArgumentOutOfRangeException`（**整个自测崩掉、不报 FAIL，还吞掉后面两条断言**）⇒ 改为**先判形状、形状对了才驱动点击** + 崩溃安全的详情取值。**教训：护栏被改坏时必须"以 FAIL 的形式变红"，崩溃会把"没测到"伪装成"没问题"**。

> **已知限制（不得当成缺陷）**：不做拖动（用户选按钮版）；`dist\` 的安装包**仍是旧启动器**（本轮没重跑安装器）；
> 非文件模式（`indexeddb` 回退）没有这枚按钮；贴纸没运行时 `placed.json` 可能过时（带 `savedAtMs`）；
> 托盘清单**不提供**"点一下收起某张便签"（收起走卡片 ❌，避免菜单里"点错就消失"）。
> **未验证项（只有用户能判）**：托盘图标「退出贴纸后是否消失」在本机**无法自动验证**（Win11 通知区是 XAML，
> `TrayNotifyWnd` 下没有 `ToolbarWindow32`，TB_BUTTONCOUNT 探针 toolbars=0/buttons=0），只有源码级证据
> （`Dispose` 里 `_icon.Visible=false` + `Dispose`，两条退出路径都调用；`BuildPinIcon` 用 `finally DestroyIcon`）
> ⇒ 请按 **W5 / W7** 肉眼确认。
> **落地前提（最重要的一条）**：`launcher/Program.cs` 改了 ⇒ 必须**关掉正在跑的那个控制台窗口、重新双击 `贴贴便签.exe`**，
> 否则网页拿不到 `/api/desktop`（按钮会提示"请重启启动器"）。**独立验证者 2026-09-21 11:31 实测**：用户当时的
> 进程 pid 16512（11:02:50 启动）仍在跑旧镜像，`GET http://127.0.0.1:8787/api/desktop → 404`。

---

### 增量 1.6 v13.1（2026-09-21 11:5x，用户实测反馈后加）

- **卡片加宽**（用户原话「功能栏太拥挤，可适当加宽贴纸」）：看板列宽 240→**300px**；`.mini-btn` / `.desk-btn` 加 `flex: 0 0 …` 防压扁。
- **点「贴到桌面」自动启动 `Sticker.exe`**：由**启动器**代劳（浏览器同样不能启动本机程序）。判"在跑吗"用贴纸自己的单实例互斥体 `Mutex.OpenExisting("Local\\TieTieSticker.S1")`，**不靠进程名猜**；只对 `place` 启动、8 秒节流；找不到就如实回 `notfound`，而**请求照样入队不丢**。回执新字段 `"sticker"`，网页据此提示「已自动启动贴纸程序…／没找到 Sticker.exe…」。
- **自动化开关**：`TIETIE_NO_AUTOSTART=1` 关掉自动启动（`bridge-e2e.ps1` 用它保住 R7/R8 那条"贴纸关闭时请求仍持久化"的判据）。
- **新脚本** `.tools\v13-autostart-check.ps1` **7/7**；回归 `v13-launcher-api.ps1 -Stage .` **35/35**、`bridge-e2e.ps1` **14/14**。
- **就地启动器本代三元组**：**105472 B / `62C37E0E4091AE03E8E7F745D8B3DE9584000671D8C98AF78AFCE80153B90F41` / 2026-09-21 11:53:49.458**（取代 v13 首版的 103424 B / `B485BE03…`）。
- ⚠️ **踩坑**：`edit` 工具重写 `.ps1` 会剥掉 BOM ⇒ 含中文的脚本按 CP936 解码、夹具 JSON 变 mojibake，症状是"GET 原样回显通过、POST 校验 400"。**每次 edit 过含中文的 `.ps1` 都要补 BOM 并重跑。**

---
### 增量 1.7 安装包（2026-09-21 12:01，用户要发给别人测试）

- **`dist\贴贴便签-安装包.exe`** = **1284096 B / `13B7C013D53FD9DFE2EEEA457B2BAADBC016CD61AC6DB875B07FB76BF7EA2D96` / 2026-09-21 12:01:39.726**；内嵌负载 **9 件 / 444620 B**。
- **本批把桌面贴纸程序也塞进包里**（`Sticker.exe` + `Sticker.exe.config`，放在启动器旁边）：启动器的「贴到桌面」自动启动就是先找 `<exeDir>\Sticker.exe`，不带它，别人点按钮只会看到 `notfound`、以为功能坏了。卸载时它随程序一起删（**不是**数据）。
- 实测（`.tools\_pkg-test`，装到临时目录，不碰真实安装点）：`--extract` 9 件且 **`Sticker.exe` 与交付产物逐位一致（`4F89E96B…`）**；`--install --no-desktop` 9 件；**装出来的启动器跑起来 `GET /api/desktop = 200`**（证明包里的是 v13 启动器）；`卸载.exe --uninstall` 后 **`data\notes.json` 仍在**、两个 exe 已删。
- ⚠️ 提醒转达测试者：自解压 exe 可能被杀毒软件拦（本机火绒的信任区只覆盖工作区），需要放行；程序**不联网、不需要管理员权限**，只要 Windows 10/11 自带的 .NET Framework 4.x。
- ⚠️ **构建脚本的坑（我自己踩的）**：`installer\build-installer.ps1` 是**纯 ASCII 文件**（R1 规则：无 BOM 时 PS 5.1 按 GBK 读，中文注释会乱码）。我插了两行中文注释 + 用了 LF 行尾，结果 `$installed` 数组**静默少了一条**（`Sticker.exe` 没进包，构建仍报 BUILD OK、manifest 只显示 8 entries）。⇒ 改这个脚本必须**纯 ASCII + CRLF**，且**必须核对 manifest 条目数与 `--extract` 的实际文件数**，不能只看 BUILD OK。

---
## 增量 2（v12，2026-09-21）：清单勾选回写

> **权威规格**：`.dsh-meow/sticker/11-S2增量规格v12-清单勾选回写.md`（本节的判据一律以它为准）。
> **一句话**：在桌面贴纸上点清单项的方框/文字 ⇒ 把 `checklist[i].done` 写回 `data/notes.json`，
> 且**绝不**吞掉网页版同时做的改动。S1 的「`notes.json` 全程只读」由此**收窄**为
> 「**读仍旧只读；唯一的写 = 用户明确点了一个勾选框**」。

### 增量 2.1 新增/改动的源码

| 文件 | 改动 |
|---|---|
| `src/NotesWriter.cs` | **新增**。产品里**唯一**写 `notes.json` 的地方：重读→逐字冲突判定→只改一项→`temp + File.Replace` 原子替换→复读+结构 diff 校验（异常即回滚原始字节）。每进程首次写前把原文另存一份到 exe 旁的 `notes-backup-before-sticker-write.json` |
| `src/Notes.cs` | `Note` 增 `UpdatedAt` 与 **`RawJson`**（该便签读入时的规范化 JSON = 冲突判定的基线）；加载器**仍然一个写符号都没有** |
| `src/Typo.cs` | `Layout` 增 `ChecklistItemY / ChecklistBoxRects / ChecklistHitRects`；新增纯函数 `ChecklistItemAt` |
| `src/CardRenderer.cs` | `DrawChecklist` 改为**从上面那批矩形画**（画与点同源，不可能漂移） |
| `src/StickerForm.cs` | 命中第 4 级 `HitZone.Checklist`；待定勾选（按下→抬起同带内生效）；超过 `SystemInformation.DragSize` 自动转为拖动；卡面**瞬时提示条**；`SetNote()` 支持重读后换内容 |
| `src/StickerManager.cs` | `ToggleChecklistItem()`（唯一写调度点）、`ReloadNotesFromDisk()`（冲突即重读重绘） |
| `src/Program.cs` | 新增 `--toggle-check <id> <idx>` 无窗口走**同一条**写路径；退出码契约见规格 §6 |
| `src/UiStrings.cs` | 三条提示文案（仍按纯 ASCII 六进制转义书写） |
| `src/SelfTest.cs` | **+18 条** S2 断言（沙盒夹具，绝不碰用户数据）；其中 **#12 `s2-bad-write-is-refused-and-rolled-back`** 是端到端回滚负例对照（首轮独立验证 F1 后补，见 §增量 2.3） |
| `demo/store.js` | **用户授权改动**：`mergeNote/mergeChecklist` 字段级合并（落盘前重读磁盘，只覆盖本次真正改动的字段）；`save/remove` 改为立即写；写入队列一次失败不再永久卡死 |
| `demo/app.js` | **用户授权改动**：编辑保存**保留原有勾选**；每次落盘只声明自己改的字段；文件模式 3 s 跟随轮询（仅页面可见且未在编辑时重载重绘） |

### 增量 2.2 复现命令（S2 新增）

```powershell
# 无窗口自测（含 18 条 S2 断言；期望 PASS=79 FAIL=0）；抓原始字节并按正例裁决解码
powershell -NoProfile -ExecutionPolicy Bypass -File .tools\capture-run.ps1 `
  -Exe .tools\sticker_build\Sticker.exe -ArgList '--selftest' `
  -OutTxt .tools\sticker_build\selftest-raw-output.txt `
  -OutBin .tools\sticker_build\selftest-raw-stdout.bin `
  -ErrBin .tools\sticker_build\selftest-raw-stderr.bin

# 端到端写回（在 data\notes.json 的**沙盒副本**上跑；期望 PASS=12 FAIL=0，并自证真文件三元组不变）
powershell -NoProfile -ExecutionPolicy Bypass -File .tools\s2-write-e2e.ps1

# 网页版合并逻辑（在沙盒里加载**真实** demo/store.js 跑；期望 PASS=9 FAIL=0）
node .tools\s2-merge-test.js

# 卡面样本重渲染 + 粉点判据 + 变异对照（期望 PASS=9 FAIL=0；临时变异构建用完即删）
powershell -NoProfile -ExecutionPolicy Bypass -File .tools\s2-pinkdot-check.ps1

# F1 自证变异（把写后校验调用点改空 ⇒ s2-bad-write-is-refused-and-rolled-back 必须变红；用完即删）
powershell -NoProfile -ExecutionPolicy Bypass -File .tools\_f1-mutation-check.ps1
```

### 增量 2.3 本批产物身份（实测，写于本节时）

| 项 | 实测值 |
|---|---|
| `Sticker.exe` | **129024 B** / SHA-256 **`A113EFBC74921640FE339C7EC419F55886F9749EF7CA62C6E69CB6C5E233224C`** / mtime **2026-09-21 07:36:05.839**（**以运行输出为准**；exe 哈希不具可复现性） |
| manifest 源条数 / 漂移 | **14 / 漂移 0**（v11.2 为 13） |
| `--selftest` | **`PASS=79 FAIL=0`**（v11.2 为 61；+18 条 S2） |
| `.tools\s2-write-e2e.ps1` | **PASS=12 FAIL=0** |
| `.tools\s2-merge-test.js` | **PASS=9 FAIL=0** |
| `.tools\s2-pinkdot-check.ps1` | **PASS=9 FAIL=0**（含变异对照：放回粉点的临时构建检出 531 个 Accent 像素，交付产物 0） |
| `interaction-test.ps1` | **本代实测 TOTAL=20 FAIL=0** —— 由**第二轮独立复核**在本代产物上跑（报告 `13-独立复核-S2第二轮.md`）；首轮 I2 的环境性抖动未复现 |

> **S2 不引入任何监听/联网符号**（存量断言 `no-listener-symbol-in-assembly` 仍绿）；
> `--render-samples` 的离屏 PNG **不含**瞬时提示条（提示只在活体窗口绘制）⇒ 样本断言口径不受影响。
> **v11.2 遗留的 CLI 缺陷已修**：`--render-samples --floating <dir>` 会把 `--floating` 当成输出目录
> （⇒ 实际没按 floating 渲染，图片落进名为 `--floating` 的目录）。现在渲染目录写在 `--floating` **前后都行**，
> 断言 `s2-cli-flag-not-eaten-as-path` 守这一点。
> **首轮独立验证（报告 `12-独立验证-S2清单勾选回写.md`）的 F1 已修**：此前用 `s2-verify-guard-detects-field-loss`
> 声称"证明写后校验不是空断言"是**过度声明** —— 它只调 `NotesWriter.Diff`，把校验**调用点**改成空操作时它仍全绿（77/0）。
> 本代新增端到端断言 **`s2-bad-write-is-refused-and-rolled-back`**（夹具含 `1.5` ⇒ 往返必有损 ⇒ 必须 `IoError` +
> 盘上字节与写前完全相同 + 无残留 temp），并由 `.tools\_f1-mutation-check.ps1` **自证可被变异打红**（control 79/0 → 变异体 1 FAIL）。

---

## 1. 源码清单（纯 ASCII 源码，只依赖 .NET Framework 自带程序集）

| 文件 | 职责 |
|---|---|
| `src/Program.cs` | 入口；参数（`--note` / `--notes` / `--verbose-hit` / `--selftest` / `--render` / `--render-samples`）；单实例互斥体 `Local\TieTieSticker.S1`；离屏 PNG 渲染 |
| `src/Json.cs` | 手写极简 JSON 解析/序列化（U2 兜底路径，**不用 `System.Web.Extensions`**，零外部依赖） |
| `src/Notes.cs` | `data/notes.json` **只读**加载 + `normalize()` 语义忠实移植；**无任何写路径**（S2 起仍成立，断言 `s2-loader-still-write-free`） |
| `src/NotesWriter.cs` | **S2 新增**：产品里**唯一**写 `notes.json` 的地方（重读→冲突判定→只改一项→原子替换→复读校验/回滚） |
| `src/Palette.cs` | 6 种便签色 + 全局色，逐值取自 `demo/styles.css@588FE4A1` |
| `src/Typo.cs` | 字体度量、贪心逐字折行、**§4.3 流式公式**、**§4.4 `MeasureNaturalHeight`**、标签折行（测量与渲染共用）、`dateLabel()`/`today()`（UTC 切片忠实复刻） |
| `src/CardRenderer.cs` | 卡面全部绘制（色带→due 药丸→旗标→标题→正文→chips→勾选清单→底部虚线+图钉）；**窗口与离屏预览共用同一份渲染代码** |
| `src/Geometry.cs` | 圆角路径、矢量图钉、矢量小钟（替代 emoji） |
| `src/StickerForm.cs` | 无边框卡窗：`Region` 圆角、`CS_DROPSHADOW`、自绘命中测试、拖动/拉伸、右键菜单、`WM_NCHITTEST→HTCLIENT` |
| `src/StickerManager.cs` | 唯一裁决点：贴出集合、`topMost` 单点仲裁、越界 clamp、写盘时机 |
| `src/State.cs` | `sticker-state.json` 加载/原子写、schema 校验与 `.bak`、越界回主屏算法 |
| `src/Log.cs` | 追加式文件日志（失败静默，绝不影响主流程） |
| `src/SelfTest.cs` | **61 项**无窗口自测（`--selftest`），含 §4.4 期望值表硬断言、**三种"改一半"反例护栏**、v11 的 ❌ 几何/命中分类/源码序守卫/state 候选表，以及 `%APPDATA%` 兜底不被写入的端到端证明 |
| `build.ps1` | 用系统自带 `csc.exe` 编译，产物**直出** `.tools\sticker_build\` |
| `interaction-test.ps1` | 合成鼠标消息驱动的交互测试（**项数以运行输出的 `TOTAL=` 为准**，当前 20 项；含 v11 的 ❌ 关闭、四类"关到 0"退出、`data\` 清单不变、盲杀护栏与 v10 规格守卫等） |
| `verify-live.ps1` | 窗口属性 / 端口 / 状态文件实测（含正例对照） |
| `audit-evidence.ps1` | **审计取证演示（3 项，独立自足）**：D1 多条 `topMost` 收敛、D2 端口正例对照、D3 `noteId` 无规范化对照实验 |

> ⚠️ **本仓库脚本编码约定**：`audit-evidence.ps1` 含中文字面量，**必须保存为 UTF-8 with BOM**——Windows PowerShell 5.1 在无 BOM 时按 ANSI 解码脚本，会把中文字符串拆坏并报 `The string is missing the terminator` 之类的解析错误。`build.ps1` / `interaction-test.ps1` / `verify-live.ps1` 全 ASCII，无此要求。

产物：`.tools\sticker_build\Sticker.exe`（**100864 B**，v11 交付版；字节数与哈希以 §5.3.1 留痕行为准）+ `Sticker.exe.config`（同名同目录）。

---

## 2. 复现命令（本机 `pwsh` 不存在，一律用 `powershell -NoProfile`）

```powershell
# 1) 构建
powershell -NoProfile -ExecutionPolicy Bypass -File desktop-sticker\build.ps1

# 2) 61 项无窗口自测（不建窗、不写状态；**断言数以 `--selftest` 输出行为准**）
.tools\sticker_build\Sticker.exe --selftest

# 3) 交互测试（项数以运行输出的 TOTAL= 为准，当前 20 项；脚本只拉起/清理自己登记的进程）
powershell -NoProfile -ExecutionPolicy Bypass -File desktop-sticker\interaction-test.ps1

# 4) 窗口属性 / 端口 / 状态文件实测（含"真起监听"正例对照）
powershell -NoProfile -ExecutionPolicy Bypass -File desktop-sticker\verify-live.ps1

# 5) 离屏渲染卡面（不需要显示器，用于肉眼核对）
.tools\sticker_build\Sticker.exe --render .tools\sticker_build\card-preview.png     # 真实 3 条便签
.tools\sticker_build\Sticker.exe --render-samples .tools\sticker_build\samples      # 合成样本

# 6) 审计取证演示（D1 收敛 / D2 端口正例对照 / D3 noteId 无规范化）
powershell -NoProfile -ExecutionPolicy Bypass -File desktop-sticker\audit-evidence.ps1
```

### 2.1 三条重点证据（审计师可直接跑第 6 条命令复现）

**D1 重启后多条 `topMost=true` 必须收敛成一条**（实施约束①）

```
D1.0 基线 state（干净）      : [0:71049138,0:27841c10,0:8a2f9604]  floated=0
D1.1 人为污染后 state        : [1:71049138,1:27841c10,0:8a2f9604]  floated=2   记录数=3
     被置顶的 id: 71049138-...e49a / 27841c10-...5394
D1.2 重启后 state            : [1:71049138,0:27841c10,0:8a2f9604]  floated=1
D1.3 CONVERGED=True   winner=71049138-30ac-44c1-97e8-0c307eece49a（= 数组序第一条）
D1.4 程序自证日志: recovery convergence: 2 records had topMost=true -> keeping only the first in array order: 71049138-...
```

**D2 端口断言的正例对照**

```
D2.1 正例：自控进程 pid 13192 真监听 127.0.0.1:8099 -> netstat 命中 1 条 LISTENING
      TCP    127.0.0.1:8099   0.0.0.0:0   LISTENING   13192
      另证 Get-NetTCPConnection -OwningProcess 13192 返回 0 条 -> 该 cmdlet 在本沙箱空洞
D2.2 被测程序 pid 24672 -> netstat LISTENING=0 TCP(任意状态)=0 UDP=0
      结论: 正例会让同名断言变红，本程序 0 条 ⇒ 断言非空洞
```

**D3 `noteId` 判定键未做任何规范化**（实施约束②）

```
D3.1 种入 3 条记录: 原样 / 尾部加空格 / 全大写
     变体1 = '71049138-...e49a '                  (尾部 U+0020)
     变体2 = '71049138-...E49A'                   (全大写)
D3.2 重启后保留 1 条: '71049138-30ac-44c1-97e8-0c307eece49a'   <- 原样匹配
D3.3 变体被丢弃、只留原样 id => 未 trim、未折叠大小写: True
D3.4 日志: state: noteId no longer present in notes.json, dropping from state: 71049138-...E49A
                                       ... dropping from state: 71049138-...e49a<空格>
```

> 若做了 `Trim()`/大小写折叠，变体会**匹配成功并贴出重复卡片**；实测是"变体被判为 notes.json 中不存在 → 丢弃"，这就是"未规范化"的**对照证据**（而非只看代码）。

> csc 选项（`/out:` `/reference:`）写在源文件之前；含空格路径整体加引号；产物由 csc 直接写出，不经 `%TEMP%`。

实测输出（关键行，本次实测）：

```
csc        : C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe
compiler   : 4.8.9221.0 built by: NET481REL1LAST_25H2
sources    : CardRenderer.cs, Geometry.cs, Json.cs, Log.cs, Notes.cs, Palette.cs, Program.cs,
             SelfTest.cs, State.cs, StickerForm.cs, StickerManager.cs, Typo.cs, UiStrings.cs
references : /reference:"C:\Windows\Microsoft.NET\Framework64\v4.0.30319\System.dll" ... System.Windows.Forms.dll
BUILD OK  100864 bytes   (v11 交付版实测；本机 csc 无 /deterministic，字节数/哈希仅对本枚构建成立，溯源以 build-manifest.json 为准)
config     : D:\work\AI项目\任务便签\.tools\sticker_build\Sticker.exe.config
strings check: no webview2 / listener tokens inside Sticker.exe
native dll check: no webview2 loader dll in the output directory
assembly refs: mscorlib, System.Windows.Forms, System.Drawing, System, System.Core
```

编译**零 warning 零 error**（13 个源文件、4 个引用程序集、无 NuGet、不联网）。

---

## 3. 验收对照

| 验收项 | 结论 | 证据 |
|---|---|---|
| 新目录 + `build.ps1` + csc 直出产物 | ✅ | §2 构建输出；无 `.csproj`、无 NuGet |
| 按规格复刻卡面 + 同套色值 + 无 WebView2 | ✅ | `--render-samples` 6 张 PNG 肉眼核对（圆角/色带/due 三态药丸/旗标/done 删除线/勾选/chips 折行/溢出裁剪/最小尺寸）；`--selftest` `palette-6-colors`；`assembly refs` 无 WebView2；`strings` 无 WebView2 字样 |
| 无边框 / `ShowInTaskbar=false` / `TopMost` 默认 false | ✅ | `verify-live`：`WS_THICKFRAME=False`、`WS_EX_APPWINDOW=False`、**`WS_EX_TOPMOST=False`**；`Process.MainWindowHandle` 为空 |
| 默认 260×320、最小 **180×190**、最大=屏工作区、底部裁剪无滚动条、图钉恒在最底部 | ✅ | `--selftest` `size-constants`/`pin-button-inside-default-card`/`pin-button-inside-min-card`/`min-size-title-fully-visible`/`min-size-title-geometry`/`default-height-justification`；`sample-4-overflow.png` 底部裁剪、无滚动条、图钉仍在最底 |
| 拖动 + 松手立即写盘 | ✅ | I1：窗口与状态同时 (40,40) → (160,120) |
| 状态持久化 / 写盘时机 / 重启复原 / 越界 clamp | ✅ | I4+I5（拉伸后重启复原 350×320）；`--selftest` `clamp-offscreen`/`clamp-visible-untouched`/`clamp-min-size`；日志只出现 `startup`/`drag-or-resize-end`/`pin-toggle`/`card-close`/`application-exit` 五种写盘原因 |
| 恢复期多条 `topMost` 收敛为一条（**硬约束 C1**） | ✅ | I8：种 2 条 → 启动后恰好 1 条且为数组序第一条；日志 `recovery convergence: 2 records had topMost=true -> keeping only the first` |
| 置顶两态 + 同一时刻只浮一张 + 下沉不 `SendToBack` | ✅ | I2/I2b/I3：浮起后恰好 1 条；浮第二张时第一张自动下沉；再点则归零。日志每次都有 `pin sink: <旧赢家>` |
| 首启贴出全部 `!archived`（P1）；`--note` 幂等 | ✅ | 首启贴 3 张（`notes.json` 3 条 `pinned` 全 false，P1 生效）；I9：`--note <已在集合里的 id>` → 0 张新卡 + 日志 `already in the placed set -> idempotent no-op` |
| 判定键 = 原始 `noteId`（Ordinal），`id` 缺失跳过（**硬约束 C2**） | ✅ | 代码 `StringComparer.Ordinal`；`--selftest` `notes-parsed` 列出原样 UUID；`state-load-order-dedup` 验证重复 `noteId` 保留首条、数组序不变 |
| 退出通道：仅「退出贴纸」一项 + `Alt+F4`（P5） | ✅ | `UiStrings.ExitSticker` 单条菜单（`Items.Add` 只调一次）；`Alt+F4` 即窗体关闭 → 移除该卡并立即写盘。**合成 Alt+F4 注入见 §5 N3** |
| 单实例互斥体（P6） | ✅ | I6：第二实例退出，常驻进程恒为 1 |
| 只读 `notes.json` + **无监听证据**（判据可用性受限，见 §5.2） | ✅（**证据边界如实标注**） | `--selftest` `notes-readonly-unchanged`：`863→863` 且 SHA-256 `A395C0C9…` **三处一致**（前置独立算 / 后置独立算 / 加载器观测值）。端口：`verify-live` V4 用 `netstat -ano` 得该 pid **0 条 LISTENING / 0 条 TCP / 0 条 UDP**，并附 V6 正例对照（自建 `127.0.0.1:8099` → 命中 1 条 ⇒ 判据非空洞）。**表述边界**：`Get-NetTCPConnection` 在本机失效，故**未取得第二种工具的独立证据**；结论应读作"**未发现监听**"，**不是**"已独立验证不监听" |照证明判据不空洞 |
| 规格 §4.3 / §4.4 排版口径 | ✅ | 起点 = `PAD_TOP`(16)（**不传 21**）；`NaturalHeight = ContentEnd + FOOT_BLOCK(40) + PAD_BOTTOM(14)`；`--selftest` `spec-4.4-notes-expected-table` = **note[0]=199 / note[1]=185 / note[2]=153**，与规格 §4.4 期望值表**逐一相等** |
| P3 标签卡面 faithful | ✅ | `TAG_CHIP_MODE=EDGE_TINT` 常量 + `--selftest` `tag-chip-mode`（alpha=102 = edge@40%）；未实现哈希配色 |
| P2 默认高度不自适应 | ✅ | `AUTO_FIT_ON_FIRST_SHOW=false`（常量）+ `--selftest` `feature-default-off` |
| P4 `today()` 忠实 UTC | ✅ | `USE_LOCAL_DATE=false` + `--selftest` `dateLabel`（今天/已逾期/普通三态，UTC 切片） |
| web 版零改动 | ✅ | `git status --porcelain` 仅 `M .gitignore`（队长所改，含 `desktop-sticker/` 条目）与 `M 对话总结.md`（队长维护）；`git status --porcelain -- demo launcher installer data` **为空**；关键文件哈希见 §6 |
| 产物与交付文件纯 ASCII 名 + `.config` 同名同目录 | ✅ | `Sticker.exe` / `Sticker.exe.config` |

### 自动测试总览

- `--selftest`：**PASS=61 FAIL=0**（含 §4.4 期望值表、4 组字面锚反例、**源码级 Measure 调用点扫描**、v11 的 ❌ 几何/命中分类/源码序守卫、兜底路径端到端断言、**build-manifest 源码↔exe 绑定**）
- `interaction-test.ps1`：**TOTAL=20 FAIL=0**（**以运行输出的 `TOTAL=` 为准**；含 v11 的 ❌ 关闭、`WM_CLOSE`/❌ 两类"关到 0"退出、`#4` 置顶关张、`#15` 1 秒窗口、`data\` 清单不变；含句柄自愈与进程计数轮询）
  - ⚠️ **已知环境敏感用例**：`I4-resize-right` 是"合成鼠标拖右边缘 +90"的**精确**断言，在宿主 GUI 争抢光标时会偏大（实测一次 `+114`，同轮其余 18 项全绿、复跑即回 `+90`）⇒ 属**合成输入抖动**、非应用缺陷；该项失败时先复跑并把两次读数一并留痕。
- `verify-live.ps1`：V1–V6 全部符合预期
- `audit-evidence.ps1`：D1 收敛 / D2 端口正例对照 / D3 `noteId` 无规范化，exit=0

---

## 4. 实现要点、已修缺陷与偏离说明

### 4.1 命中与交互
1. `WM_NCHITTEST` 一律返回 `HTCLIENT`（规格 §7.1），全部鼠标逻辑收进 `OnMouseDown/Move/Up`，保证图钉不被系统抢走命中。
2. 命中优先级：拉伸热区（边框 6px / 四角 16×16）→ 图钉按钮 30×30 → 其余任意位置 = 拖动。
3. 拖动直接改 `Location`；拉伸用 `SetBoundsCore` 绕过 WinForms 对 `MinimumSize/MaximumSize` 的静默裁剪，再在手写逻辑里 clamp 到 `[180,190] ~ 所在屏工作区`（**MIN_H 已于 2026-09-20 由 140 抬到 190**，用户拍板；几何下限 124 = 1 行标题，190 覆盖至 4 行标题）。

### 4.2 绘制
4. 文字用 `TextRenderer.DrawText`（GDI）+ 自实现贪心逐字折行，不使用 GDI `WordBreak`。
5. emoji 完全不用：矢量手绘图钉（12×12 旗标与 30×30 按钮共用同一图形）与小钟。
6. 阴影（U1）：只加 `CS_DROPSHADOW`（`CreateParams.ClassStyle |= 0x00020000`）。**注意：该位在本沙箱读数不可靠**（三次启动 1/3、2/3、0/3 置位；同进程三窗对同一类名读到三个不同 classstyle），故**不得以该位作为"已实现阴影"的证据**；按规格 §6.3 兜底，阴影缺失时只留 1px 描边。详见 §5 的 N5。

### 4.3 持久化
7. 状态文件落点（对规格 §8.1 的可辩护扩展）：首选 `<exeDir>\sticker-state.json`，**同时镜像**到 `<workspace>\data\sticker-state.json`；`%APPDATA%\TieTieSticker\` 仅作兜底，且**只在首选落点写失败时才尝试**（避免污染日志）。两处内容逐字节相同。
8. 退出语义：菜单「退出贴纸」→ 写盘后 `Application.Exit()`，**退出不删 state**（下次启动复原）；单卡关闭（`Alt+F4`/`WM_CLOSE`）→ 该卡移除并立即写盘。

### 4.4 本轮修掉的三个真实缺陷（均有复现与回归）
9. **`topMost` 不变量可能被破坏**（真 bug）：`SetTopMost` 原先只改目标记录、仅清"当前仲裁者"一条，历史漂移后可能出现**多条 `topMost=true` 同时落盘**（实测出现过 3 条同时为 true）。已改为"以仲裁者为唯一真源，逐条对齐**全部**记录"。回归：I2/I2b/I3 与 `state written ... topMostFlags=` 日志逐步验证恒为恰好一条。
10. **标签多行时测量与渲染不一致**（真 bug）：测量按 `TAG_GAP_Y(6)` 推进行距，渲染却按 `TAG_H + TAG_GAP_Y(26)` 推进 → 标签折行后**后续块整体错位 20px（每多一行）**，且与 `MeasureNaturalHeight` 不一致（会误判裁剪）。已抽出 `Typo.TagRows()` 让**测量与渲染共用同一份行划分**。回归：`sample-5-tag-wrap.png` 8 个标签折 3 行、行距 6px、行宽 228 内。
11. **构建不可复现**：同一个源码连续构建得到不同 SHA-256（PE 时间戳/MVID）。尝试加 `/deterministic+` 被本机 csc 4.8.9221 拒绝（`CS2007: 无法识别的选项`），**故明确记录：`Sticker.exe` 的哈希必须与构建时刻一起引用**，不能当"源码未变"的判据。
12. **`padTop=21` 反例护栏（自查中发现护栏自身缺陷，已修）**：加护栏时第一版把"应得值"与"错误路径值"**都从被测代码算**，导致突变到 21 时两边**一起 +5、护栏仍然绿**——这是**实跑突变**才暴露的，看代码看不出来。已改为以"按规格 §4.2 常量独立重算的块和"为锚（`measure-example1-equals-independent-blocksum`），并新增"测量路径与渲染路径必须同参"的结构不变式（`measure-and-render-padtop-agree`）。
    **突变验证**（临时把 `MeasureNaturalHeight` 的起点改成 `PAD_TOP + BAND_H` 后重编译实跑）：
    ```
    突变后 SELFTEST_EXIT=1，4 条红：
      FAIL layout-flow                        :: natural=217 (expect 212)
      FAIL measure-padTop-must-be-16-not-21   :: measured naturalHeight=309 != 309 (= 304 + BAND_H(5) = 色带被算两次)
      FAIL measure-and-render-padtop-agree     :: render-path=304 == MeasureNaturalHeight()=309
      FAIL spec-4.4-notes-expected-table       :: note[0]=204(expect 199), note[1]=190(expect 185), note[2]=158(expect 153)
    还原后 SELFTEST_EXIT=0，PASS=45 FAIL=0（当时口径：45 条断言 —— 保留原值，不改写历史）
    ```
    ⇒ `note[0]` 的 **204** 是规格 §4.4 定义 2 点名的"色带被计两次"症状，现在**任何回退都会立刻红**。
    **4 组字面锚反例**（断言名）：`example1-naturalh-literal-anchor`（要求 304）、`forbid-missing-bottom-row`（禁 264 = 304−40）、`forbid-padtop-21`（禁 309 = 304+5）、`forbid-missing-feet-on-real-notes` 与 `forbid-withdrawn-v6-values`（在**真实 notes.json 快照**上禁 159 与 204/190/158）。全部为**字面量**——第一版护栏把"应得值"与"错误值"都从被测代码算，突变时两边一起平移、护栏仍绿；改用字面锚后才真正生效（这条也是实跑突变抓出来的，不是看出来的）。
    **两种失效模式的突变实测**：
    | 故意注入的缺陷 | 实测结果 |
    |---|---|
    | `MeasureNaturalHeight` 起点改成 `PAD_TOP + BAND_H`(21) | `SELFTEST_EXIT=1`，**红 5 条**（含 `forbid-withdrawn-v6-values :: measured=204,190,158`） |
    | `NaturalHeight` 去掉 `FOOT_BLOCK`（只补一半） | `SELFTEST_EXIT=1`，**红 9 条**（含 `forbid-missing-feet-on-real-notes :: measured=159`） |
    | 还原 | `SELFTEST_EXIT=0`，PASS=45 FAIL=0（当时口径：45 条断言） |
13. **持久化日志曾"谎报"路径（审计师点名，已修）**：原写法在日志里列出**所有候选路径**，会把写失败的 `%APPDATA%` 也列成"已写"。现改为逐路径汇报真实结果：`written=[…] offered=[…] failed=[…]` —— `offered` = "兜底路径可用但本次未使用"，`failed` 只在真的失败时出现。实测：
    ```
    state written (startup): 3 record(s) -> written=[…\.tools\sticker_build\sticker-state.json | …\data\sticker-state.json] offered=[C:\Users\…\AppData\Roaming\TieTieSticker\sticker-state.json] topMostFlags=[0:71049138,0:27841c10,0:8a2f9604]
    ```
    并新增端到端断言 `fallback-offered-not-written`：在临时目录用**真实三路径**调 `Save`，要求前两条真的落盘、**第三条不落盘**。
    **保留而非删除 `%APPDATA%` 的理由**：它只在"exe 目录与 `data\` 都写不进去"时才被尝试，是唯一能保住用户位置/尺寸的兜底；配合逐路径日志，它既不再制造误导记录，也仍然真的可达（实测该路径在非沙箱下曾成功写入过）。
14. **全仓 `Measure` 调用点自查（队长要求）**：`CardRenderer.cs:23`（渲染）、`StickerForm.cs:135`（命中/布局）、`MeasureNaturalHeight` 全部传 **`Typo.PAD_TOP`(16)**；**没有任何调用点传 `PAD_TOP + BAND_H`(21)**（该表达式只出现在禁值断言的注释里）。结构不变式 `measure-and-render-padtop-agree` 自动守住"测量与渲染同参"。
15. **源码级 `Measure` 调用点扫描（防"行号漂移 + 快照过期"）**：`CheckEveryMeasureCallSiteUsesPadTop()` 在自测运行时**重新读取 `src/*.cs`**，把每个 `Typo.Measure(...)` 调用的**第 4 个实参**逐字比出来，要求全部恰为 `Typo.PAD_TOP`。这样"某个调用点被改成 21"不再依赖人工 grep、也不受行号漂移影响。实测：
    ```
    PASS measure-callsite-scan-all-padtop :: call sites=7 [CardRenderer.cs(Typo.PAD_TOP), StickerForm.cs(Typo.PAD_TOP), SelfTest.cs×5(Typo.PAD_TOP)] ; no call site uses anything but Typo.PAD_TOP
    ```
    **突变验证**：把 `CardRenderer.cs:23` 改成 `Typo.PAD_TOP + Typo.BAND_H` 后重编译实跑 →
    ```
    FAIL measure-callsite-scan-all-padtop :: call sites=6 [...] ; OFFENDERS=CardRenderer.cs: 4th arg = 'Typo.PAD_TOP + Typo.BAND_H'
    ```
    还原后 `PASS=45 FAIL=0`（当时口径：45 条断言 —— 保留原值，不改写历史）。
16. **交互测试脚本的抖动硬化**：`interaction-test.ps1` 出现过"假红"（`I4 w 0->260`、`I6 live=0`、偶发 1 条），排查后确认是**测试脚本的时序/资源竞争问题而非应用缺陷**——应用日志无任何 `EXCEPTION`/`UNHANDLED`，退出路径干净。已做两层处理：
    - **脚本硬化**：①句柄自愈 `ResolveCardHandle()`（窗口句柄失效或矩形为 0 时按记录重新解析）；②进程计数轮询 `WaitForLiveCount()`（不再对单次采样断言）；③**盲杀护栏**（captain 2026-09-20 裁令）：脚本入口发现**非本脚本登记的** `Sticker` 实例即**中止并打印 blocker（pid/启动时间/路径）**、`exit 4`，**绝不 kill**；所有清理改为 `Stop-MyStickers`（**只按 `$scriptPids`**，由 `Start-MySticker` 在每次 `Start-Process -PassThru` 时登记），**彻底移除 `Get-Process -Name Sticker | Stop-Process -Force` 这种按名强杀**；`LiveStickerProcess` 等"取第一个 Sticker"也一并改为只认自登记 pid（否则可能误把用户的卡片当靶子）。
    - **执行方式约束（三条，复跑前必读）**：① `interaction-test.ps1` 会**合成鼠标消息并拉起/清理自己的实例**，与本机前台 GUI 争抢输入队列 ⇒ **必须串行运行**，不要与其他启动 GUI/端到端脚本并行（并行时曾出现假红，例如 `I4` 拖拽偏大）；② **复跑前确认无人正在使用交付 exe**（护栏会在入口直接中止；至于 `build.ps1`：被占用时会报 `CS0016 无法写入输出文件…另一进程正在使用` —— 审计师 t4 §1.1/§1.3 实测踩过）；③ **不得杀未知的 `Sticker` 进程**（可能是用户在用）⇒ 产物被占用时改用**沙盒副本构建**或**换探针输出名**（captain 2026-09-20 操作须知）。
    - 硬化后的实跑记录（**当时口径：10 个用例** —— 保留原值，不改写历史）：`连续 6 次复跑 10/10 PASS`。
    - 现版实跑记录（**以运行输出为准**）：`TOTAL=20 FAIL=0`（exit 0）；其中 `I4` 属环境敏感项（见 §3 的"已知环境敏感用例"），本批曾出现一次 `+114` 的抖动读数、复跑即回 `+90`。

### 4.45 验收判据口径（captain 2026-09-20 采纳）

**本项目对 §4.4 的验收判据只有一个：期望值表 `note[0..2]` 的 `naturalH@260 = 199 / 185 / 153`（实现必须严格相等）。**

- 算例表的**"值"列** = 各块高度；**"累计 y"列** = 逐块累加 —— 两列**一致**（算例 1 累计到 `290`、算例 2 累计到 `264`，与"值"列相加减严格相等）。
- `ContentEnd` = **加底部行那一行之前**的 `y`：算例 1 = **250**、算例 2 = **224**、算例 3 = **145**。
- ⇒ 算例散文数字**降级为非判据**（不再作为"规格是否有误"的讨论对象）；判据以**期望值表**为准。
- **§4.4 不存在"重复计底部行"。** 曾出现的"累计列 vs 分项值差 40"说法源于**把某一行处的累计值（250）当成末值（290）去对账**，已撤回，代码/文档中不留此类记录。

### 4.455 为什么 `MIN_H = 190` 是必要的（澄清条目，captain 裁定落 README，不动规格 v10）

> 起因：审计师 t7 实测 `MIN_H` 回退 140 时 `min-size-title-fully-visible` **仍 PASS**，据此质疑该断言无拦截力。下面把"为什么 190 必要"写实。

**几何依据**（§4.2 常量 + §4.3 流式）：
```
标题块末尾 = TitleY(42) + TITLE_LH(22)·n + TITLE_MB(6) = 48 + 22n
底部行上沿 FooterTop = H − PAD_BOTTOM(14) − FOOT_BLOCK(40) = H − 54
要求标题末行不被底部图钉行压住 ⇒ 48 + 22n ≤ H − 54 ⇒ H ≥ 102 + 22n = 124 + 22(n−1)
```
| 标题行数 n | 所需最小高度 H | H=140 时 FooterTop=86 | H=190 时 FooterTop=136 |
|---|---|---|---|
| 1 | **124** | titleBottom=70 ≤ 86 ✓ 不冲突 | 70 ≤ 136 ✓ |
| 2 | 146 | **92 > 86 ✗ 冲突** | 92 ≤ 136 ✓ |
| 3 | 168 | **114 > 86 ✗** | 114 ≤ 136 ✓ |
| 4 | **190** | **136 > 86 ✗** | **136 ≤ 136 ✓ 恰好贴边** |

**三条要点**
1. **原始缺陷只在"标题 ≥2 行"时复现**：`H=140` 下 n=1 的 `70 ≤ 86` 本就不冲突，所以**用 n=1 去测，在 140 下根本不复现缺陷**。
2. **因此 `min-size-title-fully-visible`（n=1 形态）守不住 `MIN_H` 下限**——它的断言描述已显式标注该局限。`MIN_H=190` 的下限实际由**三条**守：`min-height-covers-derived-lower-bound`、`size-constants`、以及本轮新增的 **`min-size-title-4lines-fits-exactly`**（n=4 = 190 的需求方，`136 == 136` 恰好贴边 ⇒ 140 下 `86 < 136` 必然红）；另配负例 `min-height-140-is-too-small-for-2-title-lines`（`H=140` + n=2 ⇒ `92 > 86`）把**原始缺陷的复现条件**钉死。
3. **190 是"用户选定的保守产品下限"，不是几何必需**：几何上 1 行标题只需 124；190 = `124 + 22×(4−1)`，恰好覆盖到 4 行标题完整可见（来源：用户 2026-09-20 交互选择 ②，规格 v10 §7.2.1）。

**突变实测（沙盒，`MIN_H` 回退 140）**：`exit=1`，`PASS=43 FAIL=4` —— `pin-button-inside-min-card` ／ `min-height-covers-derived-lower-bound` ／ **`min-size-title-4lines-fits-exactly`** ／ `size-constants`；而 `min-size-title-fully-visible` **仍 PASS**（正是上面第 1、2 条说明的现象）。
### 4.46 护栏质检方法：突变验证（可复用，重要）

**结论：只写断言不算护栏，必须连"故意注入缺陷 ⇒ 断言变红"一起验。**

本轮的教训：`SelfTest.cs` 里第一版 `padTop=21` 护栏把"应得值"和"错误值"**都从被测代码算**，于是在把 `MeasureNaturalHeight` 的起点改成 21 时，两边**一起 +5、护栏照样 PASS**——`assert x + 5 != x` 在 `x` 本身被污染后恒成立。**看代码完全看不出来，只有实跑突变才暴露。**

**方法（每加一条护栏都做）**
1. 写断言时，把"应得值""禁值"用**字面量**固定（不要从被测代码推导）；
2. 临时把被保护的性质**改坏**（如把 padTop 改成 21、把 `FOOT_BLOCK` 去掉、把 `MIN_H` 改回 140、把某个 `Measure` 调用点改成传 21）；
3. 重编译跑 `--selftest`，确认**退出码非 0 且命中的正是预期断言**；
4. 还原并复跑，确认回到全绿；
5. 把"注入的缺陷 ⇒ 红了哪几条"记进交付说明。

**本项目的突变验证结果（全部实跑过）**

| 故意注入的缺陷 | 实测结果 |
|---|---|
| `MeasureNaturalHeight` 起点改成 `PAD_TOP + BAND_H`(21) | `EXIT=1`，**红 5 条**（`forbid-withdrawn-v6-values :: measured=204,190,158` 等） |
| `NaturalHeight` 去掉 `FOOT_BLOCK`（只补一半） | `EXIT=1`，**红 9 条**（`forbid-missing-feet-on-real-notes :: measured=159` 等） |
| 任一 `Typo.Measure(...)` 调用点改成传 21 | `EXIT=1`，`measure-callsite-scan-all-padtop` **点名文件**（`OFFENDERS=CardRenderer.cs: 4th arg = 'Typo.PAD_TOP + Typo.BAND_H'`） |
| `MIN_H` 回退 140 | `EXIT=1`，**`PASS=43 FAIL=4`**（沙盒实跑，2026-09-20 21:37）：`pin-button-inside-min-card`（`pinBottom=126 <= MIN_H=140`）／`min-height-covers-derived-lower-bound`（`MIN_H=140 >= derived 124 and >= user floor 190`）／**`min-size-title-4lines-fits-exactly`**（`titleBottom=136 == footerTop=86`，见下）／`size-constants`（要求 `MIN_H==190`）|
| 同上：为什么现在这条**真的会红** | 原 `min-size-title-fully-visible` **只测 1 行标题**（`n=1`），而 `H=140` 下 `n=1` 本就成立（`70 <= 86`，原始缺陷只在**标题 ≥2 行**时出现：`n=2 → 92 > 86`）⇒ 它给不出"190 必要"的证据（**t7 的 T1 抓到的正是这一点**）。已按 captain 裁决补两条：<br>· `min-size-title-4lines-fits-exactly` —— 测 **`n=4`（= 190 的需求方）**：`H=MIN_H(190)` 下 `titleBottom = 42+4*22+6 = 136` **恰好等于** `footerTop = 190−14−40 = 136`（再多 1px 就会被压住）⇒ **140 时 footerTop=86 < 136，必然红**；<br>· `min-height-140-is-too-small-for-2-title-lines` —— **负例**：`H=140` + 标题 2 行 ⇒ 必然冲突（`92 > 86`），把"原始缺陷的复现条件"钉死。<br>⇒ 现状：`min-size-title-fully-visible`(n=1) 仍作**边界行为断言**保留（其描述已显式标注 *LIMIT: holds at H=140 too ⇒ does NOT prove MIN_H=190 is needed*），**用户下限由 `min-height-covers-derived-lower-bound` + `size-constants` + 新的 `-4lines-fits-exactly` 三条共同守** |
| 还原 | `EXIT=0`，全绿 |
### 4.5 与规格 §15 的对齐
12. P1–P6 与硬约束 C1/C2 **逐条命中**，无"不许做"项被违反（未改哈希配色、未做高度自适应、未改本地日期、右键菜单未超 1 项）。
13. 按 §15.3 哈希纪律，本文**不内嵌规格哈希/字节数**。

---

## 5. 「未验证项」清单（无法在本环境判定，如实标注）

| # | 项 | 为什么没验证 | 影响 / 处理 |
|---|---|---|---|
| **N1** | **本机没有 `pwsh`** | `Get-Command pwsh` 找不到，只有 Windows PowerShell 5.1 | 任务给的三条 verify 里的 `pwsh` 无法原样执行；全部脚本改用 `powershell -NoProfile` 并真实跑通 |
| **N2** | **`Get-NetTCPConnection` 在本沙箱内失效** | 实测：Chrome 正监听 `127.0.0.1:3080`，`Get-NetTCPConnection -OwningProcess 9808` 返回 **0**；本进程刚 `TcpListener.Start()` 也返回 **0**。`netstat -ano` 正常 | 任务原判据**空洞**（对任何进程都说"无监听"）。已改判据为 `netstat -ano`，并用 V6 正例（自建 `127.0.0.1:8099` 监听 → netstat 命中 1 条）证明不空洞 |
| **N3** | **合成 `Alt+F4` 无法送达** | 前台锁被 DSH 的 Chrome GUI 持有，外部进程 `keybd_event` 的 Alt+F4 到不了贴纸卡 | I7 用 `WM_CLOSE` 走**同一个 `OnFormClosing`** 验证通过；**真人按 Alt+F4** 未实测 |
| **N4** | **卡面观感未做人眼实看** | 截屏被 Chrome 覆盖；改用离屏渲染自查（证明"绘制代码画出什么"，≠"屏幕观感"） | 请审计师/用户在干净桌面实看：圆角、色带、阴影、ClearType 字形 |
| **N5** | **`CS_DROPSHADOW` 是否真画出系统阴影** | **该位读数在本沙箱不可靠（实测）**：同一 exe 三次全新启动 → 带该位的窗口数 = **1/3、2/3、0/3**；且**同一进程三个窗口对同一窗口类名读到三个不同 `classstyle`**（`0x04E80834`/`0x00590B3A`/`0x001C0B5E`）⇒ 跨进程读 class-style 在此环境不可信，**我也无法截图** | **判为未验证，且不作为交付判据**。代码仍按规格 §6.3 尝试（`CreateParams.ClassStyle |= 0x00020000`），失败即自动降级为**只靠 1px 描边**——这正是规格允许的兜底，S1 不受影响。`verify-live` 的 V2 已改为如实打印该位并标注"非可靠判据" |
| **N6** | **`WS_EX_TOOLWINDOW` 位未置** | 实测 `exstyle=0x00010000`，不含 `WS_EX_TOOLWINDOW`；但 `WS_EX_APPWINDOW=False` 且 `Process.MainWindowHandle` 为空 | 验收"ShowInTaskbar=false"按**行为**通过；位标志与规格预期不符，已如实记录 |
| **N7** | **最小尺寸下标题末行被底部行压住**（原 `MIN_H=140` 时） | **已按用户拍板抬高最小高度**，`MIN_H` = **190** | ✅ **派生算式**（§4.2 常量 + §4.3 流式，标题块**只计一次**）：`MinHeightForTitleLines(n) = PAD_TOP(16) + (ROW_TOP_H+ROW_TOP_GAP)(26) + n*TITLE_LH(22) + TITLE_MB(6) + FOOT_BLOCK(40) + PAD_BOTTOM(14) = 96 + 22n + 6` ⇒ 1/2/3/4 行 = **124 / 146 / 168 / 190**；`MIN_H = max(几何下限 124, 用户下限 190) = 190`（恰好覆盖"标题 4 行完整可见"）。⚠️ **通用提醒：这个下界很容易被算成 ≈205** —— 只要把标题块的 `TITLE_LH + TITLE_MB`（22+6=28）在流式累加之外**再加一次**，就会重复计数并得到偏大的值。**来源从句以公式为准**：`Typo.MinHeightForTitleLines(n) = 96 + 22n + 6` 是唯一算式来源，`MIN_H = max(几何下限 124, 用户下限 190) = 190` 中的 190 来自**用户 2026-09-20 的交互选择**（规格 v10 §7.2.1 / §0.4 载明），不是由公式推出的 190。故以**公式 `Typo.MinHeightForTitleLines()`** 落地并配三条断言（`min-height-formula`、`min-height-covers-derived-lower-bound`、`min-size-title-fully-visible`）。**长标题在最小卡上仍按 §7.2.3 底部裁剪**（图钉可点优先）——既定行为，非缺陷。原"按规格不动（140）"的旧裁定**已作废** |
| **N8** | **§4.4 算例表的"值"列与"累计 y"列是否一致** | 算例 1 累计列 `16→42→70→146→175→250→290` 逐行差值 = `26/28/76/29/75/40`，与"值"列**一一对应，每步只加一次** | ✅ **不存在重复计 40**（captain 2026-09-20 逐行核算裁定）：`250` 是清单块之后的累计值（底部行**之前**的位置，即 `ContentEnd`），`290` 是**含**底部行的 `y`；两者是"逐块累加"与"求和"同一结果，**相等**。实现与期望值表严格一致；`NaturalHeight = ContentEnd + FOOT_BLOCK(40) + PAD_BOTTOM(14)` |
| **N9** | **U5 高 DPI 观感** | 本机 100%（`GetDpiForWindow=96`），无高分屏 | 未验证；进程 DPI-unaware（`awareness=0`） |
| **N10** | **真实鼠标点击** | 卡是普通顶层窗，被前台 Chrome GUI 整屏覆盖，`WindowFromPoint` 永远返回 Chrome | 改用 `SendMessage` 直接向卡 hwnd 投递 `WM_LBUTTONDOWN/MOUSEMOVE/LBUTTONUP`（同步），驱动的是**同一套** `OnMouseDown/Move/Up`；"真人用鼠标拖"未实测 |
| **N11** | **`Form.Size == ClientSize`（U4）在句柄创建瞬间不成立** | `OnHandleCreated` 读到 `260x320 / 254x291`；到 `OnShown` 与首次 `OnPaint` 恢复为 `260x320/260x320`（窗口矩形始终 260×320） | 判定为句柄创建期瞬时读数；绘制与命中都用 `ClientRectangle`（实际恒等于窗口尺寸）。日志保留三处采样 |
| **N12** | **构建哈希不可复现** | 见 §4.4 第 11 条（本机 csc 不支持 `/deterministic`） | **不用 exe 哈希作"源码未变"判据**；改由 `build-manifest.json` 绑定 exe↔源码树，并用 `--selftest` 的 `build-manifest-exe-digest` / `build-manifest-source-tree-unchanged` 两条断言自证（详见 §5.3） |
| **N13** | **含中文的 `.ps1` 必须带 UTF-8 BOM** | Windows PowerShell 5.1 在无 BOM 时按 ANSI 解码脚本；`audit-evidence.ps1` 首版因此报 `The string is missing the terminator` 解析失败 | 已给该文件加 UTF-8 BOM 并 `Parser::ParseFile` 验证 `PARSE OK`；另三个脚本全 ASCII，无需 BOM。**后续任何人编辑该文件后必须保留 BOM** |
| **N14** | **§4.4 的"验收判据"是哪一个** | §4.4 既有算例表（"值"列 + "累计 y"列）又有期望值表，读者可能把算例散文数字误当判据 | ✅ **收口口径（captain 2026-09-20 确认）**：**规格 §4.4 期望值表（`note[0..2] = 199 / 185 / 153`）为实现验收判据**；算例表的"值"列为**各块高度**、"累计 y"列为**逐块累加**，**二者一致**（算例 1 累计到 290、算例 2 累计到 264，均与"值"列相加严格相等）。自测的其余护栏（`forbid-missing-bottom-row`=禁264、`forbid-padtop-21`=禁309、`forbid-withdrawn-v6-values`=禁204/190/158、`measure-callsite-scan-all-padtop`）均为**字面锚 + 突变验证**过的回归护栏 |

---

## 5.1 P1「`pinned` 只读不筛」的取证（F5 结案项，供审计复核）

| 位置 | 用途 | 是否参与"贴哪些便签" |
|---|---|---|
| `src/Notes.cs:163` | `n.Pinned = Boolish(raw.Get("pinned"))` —— 解析 | ❌ 只解析 |
| `src/CardRenderer.cs:87` | `if (note.Pinned)` —— t1 §3.2 卡面右上角图钉标记 | ❌ 仅渲染 |
| `src/SelfTest.cs:335` | 自测日志打印 | ❌ 仅日志 |
| `src/StickerManager.cs:91` | `if (n.Archived) { skip }` —— **首启贴出唯一过滤条件** | ✅ 唯一过滤是 `archived` |

⇒ 全仓库**没有任何一处**用 `pinned` 过滤贴出集合；`notes.json` 的 `pinned` 与 `sticker-state.json` 的 `topMost` 是两套互不相干的字段（代码中无交叉引用）。实测首启贴出 **3 张**（本机 3 条便签 `pinned` 全为 `false`，若按 v1 的 `pinned` 口径会贴 0 张）。

---

## 5.2 判据说明：端口断言为什么用 `netstat -ano`

**结论：本项目的"应用自身不监听端口"判据一律用 `netstat -ano`，禁止使用 `Get-NetTCPConnection`。**

理由（实测，非推断）：

| 方法 | 对 Chrome 的 `127.0.0.1:3080` | 对自建 `127.0.0.1:8099` 监听 | 可用性 |
|---|---|---|---|
| `Get-NetTCPConnection -OwningProcess <pid>` | 返回 **0 条** | 返回 **0 条** | ❌ **空洞**：对任何进程都输出"无监听" |
| `netstat -ano` | 命中 1 条 `LISTENING` | 命中 1 条 `LISTENING` | ✅ 可用 |

⇒ `Get-NetTCPConnection` 在本机**无区分力**（本项目已吃过一次"假绿"的亏）。因此 `verify-live.ps1` 的 V4 用 `netstat -ano` 过滤本进程 pid，并且**必须同时做一次正例对照**（另起进程真监听 `127.0.0.1:8099` → netstat 命中 1 条 ⇒ 输出 `-> the port assertion is LIVE (non-vacuous)`）。**没有正例对照的端口判据视为无效判据。**

> ⚠️ **证据边界（必读，勿把"未发现"读成"已验证不存在"）**
> 上表说明的是**判据可用性**，不是"已经不监听"。本机只有 `netstat` 一条可用路径，因此本轮**未取得第二种工具的独立证据**。正确表述是：
> **"在唯一可用的判据（`netstat -ano` + 正例对照）下未发现本程序持有任何监听/连接端点"**；
> **不可**表述为"已独立验证本程序不监听"。前者是已取得的证据，后者超出了本轮能做的方法学范围。
> （同一条边界也适用于 §5.2 下文的只读证据：`SHA-256 前后一致 + 加载器观测值一致`是**已取得**的证据；"代码里没有任何写路径"是**静态审查**结论，两者分开陈述。）

`--selftest` 的 `notes-readonly-unchanged` 同理：**只比字节长度不构成证据**（写入可以保持长度不变），必须比 **SHA-256**——实测 `data/notes.json` 在加载前后 `863→863` 且摘要 `A395C0C9…` 三次一致（前置独立计算 / 加载后独立计算 / 加载器自己观测到的值）。

---

## 5.3 如何证明「某个 exe 对应某份源码」

**本机 csc 4.8.9221 不支持 `/deterministic`（`CS2007`），所以同一份源码连续构建会得到不同的 exe 字节**（PE 时间戳 / MVID 不同）。因此：

- ❌ `Sticker.exe` 的 SHA-256 **不能**用作"源码未变"的判据；
- ✅ `build.ps1` 每次构建写出 **`build-manifest.json`**，把 exe 与源码树**绑定**：

```json
{
  "app": "tietie-sticker-s1",
  "builtAtUtc": "2026-09-20 12:55:11",
  "compiler": "4.8.9221.0 built by: NET481REL1LAST_25H2",
  "exe": { "file": "Sticker.exe", "bytes": 88576, "sha256": "…" },
  "sources": [ { "name": "Typo.cs", "bytes": 15297, "sha256": "…" }, … ]   // 13 个源文件
}
```

验证方式（**自证，无需人工比对**）：`--selftest` 的两条断言
```
PASS build-manifest-exe-digest            :: manifest says <hash>, actual Sticker.exe <hash> (match)
PASS build-manifest-source-tree-unchanged :: sources matched 13/13 (current tree == tree this exe was built from); builtAtUtc=…
```
即：**manifest 里记的 exe 摘要 == 磁盘上 exe 的实算摘要**（证明 exe 未被替换），且**manifest 里每个源文件的摘要 == 当前源文件实算摘要**（证明当前源码树就是构建这枚 exe 的那棵树）。
若源码改动后忘记重建，第二条会红并列出 `drifted=…`；若 exe 被替换，第一条会红。

**⚠️ manifest 的「覆盖边界」（captain 2026-09-20 追加；依据 t12 实测）**：`build-manifest.json` **只绑定 `desktop-sticker/src/*.cs`（13 个）与 `Sticker.exe` 本身**，**不包含任何 `.ps1` 测试/构建辅助脚本，也不包含 `README.md`**。
- ⇒ **交付后修改 `interaction-test.ps1` / `verify-live.ps1` / `audit-evidence.ps1`（或 README）不会触发任何 manifest 告警**（实测：`interaction-test.ps1` 曾于 22:19:43 变化而 manifest 未动）。⇒ **脚本/文档的版本必须另行记录三元组（字节 + SHA-256 + mtime）**，本 README 的 §3 与 §5.3.1 即按此留痕。
- ⇒ 反过来说：**manifest 全绿只证明"这枚 exe 来自这 13 个 .cs"**，**不证明**测试脚本/文档保持冻结。引用"未改动"结论时请分清这两层。

### 5.3.1 留痕行：当前交付产物 ←→ 源码时刻（v11 增量，t10 批次 + t11 后加固批次）

**本次交付产物（由 `desktop-sticker/build.ps1` 在 `.tools\sticker_build\` 直出，实测值，非声明）**

| 项 | 实测值 |
|---|---|
| `Sticker.exe` 字节数 | **100864 B** |
| `Sticker.exe` SHA-256 | **`C1A9DF26A0C7310565CC5B82D8CD84AE33FB4EF674D5215739C4633B70664794`** |
| `Sticker.exe` mtime | **2026-09-20 22:05:20** |
| `build-manifest.json` `builtAtUtc` | `2026-09-20 14:05:20`（= 22:05:20 +08:00） |
| manifest 源码条数 / 漂移 | **13 条 / 漂移 0**（manifest 逐项 == 磁盘 `desktop-sticker/src/*.cs`） |
| 编译绑定 | **成立**：`max(mtime(src/*.cs), mtime(build.ps1)) = 22:05:00 < mtime(Sticker.exe) = 22:05:20` |
| `--selftest` | **`PASS=61 FAIL=0`，退出码 0**（全日志 65 行；行首锚定 `^\s*PASS  ` = 61、`^\s*FAIL  ` = 0） |
| `interaction-test.ps1` | **TOTAL=20 FAIL=0**（**以运行输出的 `TOTAL=` 为准**；含盲杀护栏、I16 v10 哈希守卫与 v11 新增 I17/I18/I19） |
| 断言计数口径 | 61 = 上一交付版 45 + 本增量新增 **16**（几何 11 + 源码守卫 3 + 2 条 v10 遗留新断言） |

**"当前产物对应哪份源码"的判据**（本机 csc 4.8.9221 无 `/deterministic`，exe 哈希每次构建都会变）：
**不得**用 exe 哈希证明"源码未变"；**用 `build-manifest.json`** —— 它记的 exe 摘要 == 磁盘 exe 实算摘要（`build-manifest-exe-digest`），且它记的 13 个源文件摘要 == 当前 `src/` 实算摘要（`build-manifest-source-tree-unchanged`）。**本批次的"源码时刻" = 上面那张表的 mtime 与 manifest `builtAtUtc` 同时成立的那一刻**。

**本批次纳入的改动（源码层面）**：`Typo.cs`（`CLOSE_*` 常量 + `CloseHitRect/CloseLeft/FlagRight` + `HitLevel` 与纯函数 `ClassifyHit`）、`Geometry.cs`（`DrawClose`）、`CardRenderer.cs`（📌 左移 `CLOSE_BOX+CLOSE_GAP`、右上角常显自绘 ❌）、`StickerForm.cs`（命中优先级 4 级 + ❌ 按下不武装拖动 + 抬起仍在同一命中盒内才关）、`StickerManager.cs`（**先移除记录 + `Persist("card-close")`，再判 `CardCount==0` 才 `ExitAll`**；空集 + `--note` 抑制"贴全部"）、`Program.cs`（第二实例可见提示 `MessageBox`）、`UiStrings.cs`（提示文案，仍按 ASCII 转义书写）、`State.cs`（state 候选**收敛为 2 条**，删除 `<workspace>\data\sticker-state.json` 第三条）、`SelfTest.cs`（+16 断言）。规格：`.dsh-meow/sticker/06-S1增量规格v11-关闭按钮与空集重贴.md`（v10 主规格 `937ED89C…` 一字未改）。

**两处必须一并留痕的批次副作用**：
1. **测试会覆写 `.tools\sticker_build\sticker-state.json`**（`interaction-test.ps1` / `audit-evidence.ps1` 都靠改 state 造场景），跑完会留下测试用的记录集。**用户 2026-09-20 21:51:16 的排列快照**（`71049138… 40,40 180x320` / `27841c10… 775,287 260x190` / `8a2f9604… 474,123 260x190`）**不属于程序产物**；它曾于 **22:10** 被人工逐字节写回过一次：`589 B / SHA-256 AB3ED1FAAE171B5A6573EE0CB42E36E57231E256091CFC1B024A316CF02D98FD`（= 队长授权删除的 `data\sticker-state.json` 原内容的哈希，**逐字节相同**）。
   - **⚠️ 时限说明（captain 2026-09-20 指定措辞）**：**该次人工恢复发生于 22:10；此后取值以现场实测为准**。其后该文件又被验证运行改写成默认瀑布/2 条记录、并被我清掉测试残留；队长后来**撤销**了"写回用户排列"一事（**不伪造运行时状态**）⇒ **交付态应为"该文件不存在"**，下次启动按规格 §10.1-2 **首启重贴全部 3 张**（默认瀑布）。**要不要恢复该排列，由用户决定**（届时性质是"按用户要求恢复"，与"我们代他伪造"不同）。
   - ⚠️ **另有一条独立的环境事实（非本批引入）**：`%APPDATA%\TieTieSticker\sticker-state.json` 在本机确实存在（实测 `416 B / 22:28:42 / C5956F10…`），而 v10 §8.1 明写该回退路径**只写不读**；实测**主路径缺失时应用会读回退**（日志 `state loaded from …AppData\Roaming\TieTieSticker\sticker-state.json: 2 record(s)`）。⇒ 这是**既有实现与规格 §8.1 的差异**，本批**不重建 exe、不修**；已上报队长与分析师作 t12 候选 finding；测试脚本因此改为**写"空 `stickers` 的主路径 state"**（规格 §10.1-2 同样触发首启重贴）而不是删文件，以与回退文件解耦。
2. **捕获层为什么不能裸用管道**：`--selftest` 的输出是**子进程按 CP936（系统 ANSI 代码页）写出的字节**，而交付 exe 是 `/target:winexe`（无控制台）。若用 PowerShell 管道捕获（父进程按 UTF-8 解码），中文会被替换成 `U+FFFD` —— 21:29 那份旧归档就是这样被有损破坏的（20 个 `U+FFFD`、`data\notes.json` 路径不可核）。本批固定用 **`System.Diagnostics.Process` + `BaseStream` 抓原始字节** → 按 **CP936** 显式解码 → 写 UTF-8(BOM) 的 `selftest-raw-output.txt`，并同时保留 `selftest-raw-stdout.bin` / `selftest-raw-stderr.bin` 原始件。**正例对照**：解码后必须**完整包含** `D:\work\AI项目\任务便签\data\notes.json`（33 字符）且 `U+FFFD` 计数 = 0；**反例对照**：同一份 `.bin` 按 UTF-8 解必须**不包含**该串（否则该正例没有分辨力）。本批实测：正例 True / `U+FFFD`=0，反例 False / `U+FFFD`=54 ⇒ 对照有牙。
3. **`Start-Process -RedirectStandardOutput` 在本机被拒**（`Access is denied`）⇒ 只能用上面的 `Process` + `BaseStream` 方式；这也顺带解释了历史上"`--selftest` 拿到空输出"的现象（不是程序没输出，是**捕获方式**失败）。
4. **测试脚本会删 `data\sticker-state.json`（若存在）—— 这是测试行为，不是程序行为**：`interaction-test.ps1` / `verify-live.ps1` 的 fresh-start 里会清理这个**历史第三条候选**遗留物（v11 §8.1 已把该候选从实现里删除 ⇒ 当前构建**永不**产生它，故这一步实际是 no-op）。**程序本身对 `data/` 是只读的**（不创建、不修改任何文件；`data\notes.json` 与 `.bak` 的字节/SHA-256 由 I14/I15 逐项核对）。留痕原因：日后若看到 `data/` 被"脚本动过"，应判为**测试脚本的清理动作**，不得误判为应用写入了受保护目录。
5. **日志策略：先归档，再清**（captain 2026-09-20 裁令）：脚本 fresh-start 曾直接删除 `sticker-debug.log`，导致"每跑一批就毁掉一批运行期证据"（21:42–21:51 那段会话已不可复读）。现改为**先 `Copy-Item` 成 `sticker-debug.<yyyyMMdd-HHmmss>.log` 再清**，并在输出里打印**归档文件名 + 被归档段的起始时间戳 + 本次段的起始时间戳**（`interaction-test.ps1` 内联、`audit-evidence.ps1` 用 `Archive-Log`）。**清日志的是脚本，不是 exe**：`src/Log.cs` 只使用 `File.AppendAllText`（建/追加），从不截断 —— 故本批未改 `src/*.cs`、未重建 exe。

---

## 6. 零改动自证（最终态，实测）

```
$ git status --porcelain
 M .gitignore              <- 队长所改（新增 desktop-sticker/ 条目），非本轮改动
 M 对话总结.md              <- 队长维护，非本轮改动

$ git status --porcelain -- demo launcher installer data
(空)
```

> ⚠️ **`data\` 的证据方法（v11 §8.2 固化，必须照此）**：`.gitignore` 第 3 行是 `data/`，**整个 `data/` 目录被忽略** —— `git check-ignore -v data/notes.json` **也命中**。因此 `git status --porcelain -- data` 对 `data\` 内**任何**写入（**包括 `data/notes.json` 本身被改**）都**恒为空**：它是「**未检出**」，**不是**「**未发生**」。
> ⇒ **`data\` 一律用「目录清单 + 逐文件字节 + SHA-256」自证**（下表即此法）；`git status` 只能用于**未被 ignore** 的 `demo/`、`launcher/`、`installer/`、根 `贴贴便签.exe`。
> ⇒ 本批次实测：`data\` 仅含 `notes.json` 863 B（mtime 2026-09-11 19:35:05）与 `notes.json.bak` 596 B —— **不存在 `data\sticker-state.json`**（v11 §8.1 已把该候选从 `State.cs` 删除；删除前由队长授权清理，删除件留档 589 B / `AB3ED1FA…`）。

| 文件 | 字节 | SHA-256 | 与开工前 |
|---|---|---|---|
| `demo/index.html` | 4559 | `186513583200CDBA30160111B8FCEB3AA2FA996EF156A71DB8B6CD75BC543D79` | 一致 |
| `demo/styles.css` | 14329 | `588FE4A187BF09FD8E0D3D5C461C9BCCCE0E74474CD80B10CE19BF6333B1E293` | 一致 |
| `demo/app.js` | 22600 | `A14ADA9757875C1D1EB8F9073511EC3DACA4F24DB911DC2E4EEF0792C992EF3A` | **S2 已改**（用户②授权；旧值 18337 B / `85CA714C…`） |
| `demo/store.js` | 9309 | `B4653585D5F63921E5264EF4D17E2E3BA63E0D6DB714024F860D69A8DEAA745A` | **S2 已改**（用户②授权；旧值 6339 B / `D04640A3…`） |
| `data/notes.json` | 863 | `A395C0C91AAAE644A0486B5258C523FC64E897A38ECE1310A0944AA45F91E0A1` | 一致 |
| `贴贴便签.exe` | 92672 | `BE5A273260FA1CC68F9A2FF3B7CD4A0C76504F29FE32560B43350CC3F2E60399` | 一致 |

> 规格文档哈希按 §15.3 不在此内嵌；需要时以**实测值 + 实测时间**为准。
> **S2 起口径更新**：贴纸**不再**对 `data/notes.json` 全程只读 —— **唯一的写**是"用户点了清单勾选框"，
> 只经 `src/NotesWriter.cs`（原子替换 + 复读校验 + 失败回滚）。加载路径仍然一个写符号都没有。
> `data\sticker-state.json` 是贴纸自己的状态文件（与 `notes.json` 无关），v11 起已从候选表删除。

---

## 增量 1.8（v14，2026-09-21）—— 合并为单文件 + 任务栏窗口

**用户原话**：「把Sticker.exe整合进贴贴便签.exe,并且完善屏幕下方任务栏中的窗口显示」。
用户拍板：整合方式 = **A 单文件双模式**；任务栏 = **A 换成正规窗口**。权威规格 = `.dsh-meow/sticker/16-增量规格v14-合并单文件与任务栏窗口.md`。

**交付形态变了**：交付目录里**只有一个 exe**。

| 件 | 角色 | 本代身份 |
|---|---|---|
| `贴贴便签.exe` | **交付主程序**（启动器 + 贴纸两套代码都在里面） | 260096 B / `F17780C7F15D8D0961B71E528EC20A1BA054B2AEA1A7CDB1E5E910B984F2339F` / 12:32:54.083 |
| `贴贴签.build-manifest.json` | 该 exe ↔ 18 个源文件的绑定（漂移必须 0） | 3628 B / `F413FA9E…` |
| `dist\贴贴便签-安装包.exe` | 安装包（负载 **7 件**） | 1291264 B / `5D00D903…` / 12:35:06.351 |
| `.tools\sticker_build\Sticker.exe` | **仅开发/验证用**（自测、出图、交互测试、桥协议），**不进安装包** | 158720 B / `D84281BA…` |

**构建**：`launcher\build.ps1` 现在是合并构建（18 源 + `/target:winexe` + `/main:TietieLauncher` + 合并版 manifest）。`desktop-sticker\build.ps1` 不变（仍产出独立 `Sticker.exe` 供验证用）。

**运行期派发**：`Main` 首段认贴纸专用开关（`--sticker/--selftest/--note/--notes/--verbose-hit/--render/--render-samples/--floating/--toggle-check`）⇒ 交给 `TieTieSticker.Program.Main`（已改 `internal`）。`--headless` = 启动器只跑服务、不建窗口（自动化专用）。

**新增控制请求**：`action:"exit"`（`POST /api/desktop` 或内部 `AppendExit()`）—— 关窗口时请贴纸干净退出。`noteId` 可为空；贴纸侧 `ExitIsFresh` 时效围栏 120 s，**无时间戳/过旧一律不认**（request.json 比两个进程长寿）。

**本批修掉的既有真缺陷（v13 就存在）—— ack 顺序**：`Program.cs` 先挂发布钩子、后 `mgr.Start()`，而 `Start()` 的首个 `Persist("startup")` 会 `Publish()`；v13 里 `_ackSeq` 要到 `bridge.Start()` 才读回 ⇒ **首发布把 ackSeq=0 写进 placed.json** ⇒ 读回 0 ⇒ **request.json 全部历史请求被重放**（旧 `remove` 关掉屏幕上的卡、旧 `place` 把卡按瀑布位重摆）。修法 = **读回移到构造函数**；护栏 = 自测 2 条 + 变异 M5。**headless 自测 87 条全绿也没测到它**——是 `interaction-test` 活体跑批逼出来的。

**脚本变化**：
- 新增 `.tools\v14-merge-check.ps1`（17 项：PE 子系统 GUI vs 旧镜像控制台、两半都在、窗口/关窗、无 Sticker.exe 仍自启动、exit 端到端、headless 无窗口、用户数据未动）
- 新增 `.tools\v14-installer-check.ps1`（11 项）、`.tools\v14-mutation-check.ps1`（8 项）
- `v13-launcher-api.ps1`（35→**37**，加 E1/E2）、`bridge-e2e.ps1`（14→**16**，加 R12/R12b；贴纸半 = 同一 exe `--sticker`）
- `v13-autostart-check.ps1` → **`_superseded-v13-autostart-check.ps1`**（其 A6「Sticker.exe 移走 ⇒ notfound」前提已被合并消灭；覆盖移交 v14-merge-check M5–M7）

**本代实测**：合并版 `--selftest` **PASS=93 FAIL=0**；`v14-merge-check` 17/17；`v13-launcher-api` 37/37；`bridge-e2e` 16/16；`v14-installer-check` 11/11；`v14-mutation-check` 8/8；`interaction-test` **20/0**；s2 三项 12/9/9；`data\notes.json` 全程只读。

## 增量 1.9（v15，2026-09-21）—— 启动就恢复桌面贴纸 + 置顶状态变粉

用户原话（v14 手工验收回报）：

> 「退出后重启,桌面上贴的便签不能保留.桌面上便签置顶后❌左面的置顶状态符号不会变粉」

两条都是**真缺陷**。权威规格 = `.dsh-meow/sticker/18-增量规格v15-启动恢复与置顶粉旗.md`。

| 件 | 角色 | 本代身份 |
|---|---|---|
| `贴贴便签.exe` | **交付主程序**（启动器 + 贴纸两套代码） | 263680 B / `D0F7AEBB05E77503B46B9410E525A2B608FE6527DFD437445799032375D30EF3` / 16:59:41.795 |
| `贴贴便签.build-manifest.json` | 该 exe ↔ 18 个源文件的绑定（漂移 0） | 3628 B / `0C0397B7…` |
| `dist\贴贴便签-安装包.exe` | 安装包（负载仍 **7 件**） | 1300992 B / `C48CE1CB…` / 16:59:43.295 |
| `.tools\sticker_build\Sticker.exe` | **仅开发/验证用**，**不进安装包** | 161280 B / `8A9F8C09…` |

**缺陷①：状态没丢，是没人读它。** 关窗口（v14 口径）= 贴纸一起干净退出并**写好** `sticker-state.json`；但全程序**只有一处**会启动贴纸（`POST /api/desktop` 的 `place` 分支）⇒ 重启后没有任何人再把它叫起来，桌面自然空。修法 = `launcher/Program.cs` 在窗口就绪后调 `StickerStarter.EnsureStartup(exeDir)`：贴纸起来后自己读存档、**原样**贴回（位置/大小/置顶），存档为空集时走首启规则（全部重贴，用户 9/20 定的口径）。既有护栏不变（互斥体判"在没在跑"、8 s 节流、`TIETIE_NO_AUTOSTART=1`）。新增一个**只关启动恢复**的自动化开关 `TIETIE_NO_STARTUP_STICKER=1`，让"网页点『贴到桌面』按需启动"这条老路径仍可单独测（`v14-merge-check` 的 M5–M8 就是它）。`--headless` 走不到这段，自动化不受影响。

**缺陷②：❌ 左边那个图钉画的不是"桌面置顶"。** 它画的是 `note.Pinned`（网页里置顶过这张便签，只读、恒灰）；"浮在最上层"只画在**左下角那个图钉按钮**上。修法 = `CardRenderer.DrawTopRow` 增加 `floating` 入参：`note.Pinned || floating` 都画，**`floating` 时用 Accent(`#FF7A9E`)**，只网页置顶时仍是灰。几何不动（旗盒 `208,20,12,12`），与 v11.2 删掉的粉点区 `(234,39,10,10)` 不重叠。

**脚本变化**：
- 新增 `.tools\v15-autostart-check.ps1`（6 项：启动即拉起贴纸半 / 按存档坐标还原 / 只贴存档里那张 / 读到 `1 record(s)` 且无 `first-run placement` / 关窗仍两半都退 / 自动化开关双判据）。可用 `-Exe <变异体>` 复用。
- 新增 `.tools\v15-mutation-check.ps1`（7 项：control 绿且**确实带着**那 3 条新断言；M1 删启动恢复**调用点** ⇒ A1 红；M2 恒灰旗、M3 只画网页置顶 ⇒ 同一断言红；变异体用完即删；交付产物未动）。
- `v14-merge-check.ps1`：M3 阈值 88→96；M4 与 M5–M8 改用 `TIETIE_NO_STARTUP_STICKER`（启动恢复不再污染这两组判据，v14 的请求路径判据原样保留）。
- `s2-pinkdot-check.ps1`：新增两条旗色正/负对照（浮动样本旗盒有 Accent / 普通样本 0）。

**自测新增 3 条**（离屏绘制 + 3× 放大后逐像素精确比色，不建窗口、不写状态、不碰用户数据）：`v15-top-flag-accent-when-floating`、`v15-top-flag-absent-when-neither`、`v15-top-flag-gray-when-only-web-pinned`。

**本代实测**：两半 `--selftest` 都 **PASS=96 FAIL=0**（87 + v14 的 6 + 本批 3）；`v15-autostart-check` 6/6；`v14-merge-check` 17/17；`v13-launcher-api` 37/37；`bridge-e2e` 16/16；`v14-installer-check` 11/11；`v14-mutation-check` 8/8；`v15-mutation-check` 7/7；`s2-pinkdot-check` 11/11；`interaction-test` **20/0**；s2 写回/合并 12/9；`data\notes.json` 全程只读（867 B / `27E0D5FB…`）。

## 增量 1.10（v16，2026-09-21）—— 删掉 ❌ 左边的置顶图钉 + 每个贴纸各占一个任务栏窗口

用户原话（一条紧接 v15 的指令）：

> 「只有一个桌面贴纸置顶时,贴纸才能显示正常(且永远只有一个贴纸显示❌左面的图钉标志).且任务栏的贴纸窗应该预览贴纸窗口内容(有几个贴纸就有几个窗)」

> 「**直接删去❌左面的置顶图钉标志,然后实现任务栏显示窗口的功能**」

⇒ **v15 的缺陷②修法（❌ 左边图钉变粉）被本批撤销**；v15 的缺陷①修法（启动就恢复桌面贴纸）**一行未改**。权威规格 = `.dsh-meow/sticker/19-增量规格v16-删置顶图钉与任务栏窗口.md`。

| 件 | 角色 | 本代身份 |
|---|---|---|
| `贴贴便签.exe` | **交付主程序** | 263168 B / `B6F7CDB46C710B466CD5F49A09D4693544766164A564F5950A0EC0936436EB3C` / 17:12:56.568 |
| `贴贴便签.build-manifest.json` | 该 exe ↔ 18 个源文件（漂移 0） | 3628 B / `0BAF3B5D…` |
| `dist\贴贴便签-安装包.exe` | 安装包（负载仍 **7 件**） | 1299456 B / `4929DA6F…` / 17:12:58.112 |
| `.tools\sticker_build\Sticker.exe` | **仅开发/验证用** | 160768 B / `EEA9906D…` |

**改动只有两处代码**：
1. `CardRenderer.DrawTopRow`：把 `if (note.Pinned || floating) { Geometry.DrawPin(...) }` **整块删除**（`DrawTopRow` 不再收 `floating`）。此后那个位置**恒为背景色**——灰旗、粉旗都没有；置顶状态**只**由左下角图钉按钮表达（浮动时 Accent，它同时是开关）。
2. `StickerForm` 构造函数：`ShowInTaskbar` `false → true`（WinForms 随即置 `WS_EX_APPWINDOW` ⇒ 每张贴纸一个任务栏按钮；标题本来就是便签标题，悬停即 DWM 内容预览），并加 `MinimizeBox/MaximizeBox = false`（否则无边框卡片的任务栏右键菜单会提供"最大化"，一点就是坏掉的全屏贴纸）。**这条推翻了 S1 的「不进任务栏 / `ShowInTaskbar=false`」口径**（用户明确要求），历史证据文件保留但不得再当现行判据。

**脚本变化**：新增 `.tools\v16-taskbar-check.ps1`（5 项：3 张贴纸=3 个带 `WS_EX_APPWINDOW` 的卡片窗口 / 标题=便签标题 / **启动器窗口带该位作正对照** / 都未被 DWM cloaked / 关窗后不留孤儿窗口）、`.tools\v16-mutation-check.ps1`（8 项：control 带 2 条 v16 断言 + M2 旗用 Accent 画回来 ⇒ 红、M3 用灰色画回来 ⇒ 同一条红、M1 删启动恢复调用点 ⇒ A1 红、M4 `ShowInTaskbar=false` ⇒ B1 红 + 清理 + 产物未动）。`s2-pinkdot-check.ps1` 的两条 v15 旗色判据换成 v16"旧旗位无 Accent"；`v14-merge-check.ps1` M3 阈值 96→95；**删除 `.tools\v15-mutation-check.ps1`**（补丁锚点随源码消失，留着会抛 `patch target not found`）。

**自测**：删 3 条 v15 旗色断言、加 2 条 → 两半都 **PASS=95 FAIL=0**（`v16-top-flag-removed` 覆盖 `(note.Pinned, floating)` 四种组合的 **ink==0**；`v16-footer-pin-is-the-only-floating-marker` 浮动时 1503 个 Accent 像素、不浮动 0）。

**本代实测**：`v16-taskbar-check` 5/5；`v16-mutation-check` 8/8；`v15-autostart-check` 6/6；`v14-merge-check` 17/17；`v13-launcher-api` 37/37；`bridge-e2e` 16/16；`v14-installer-check` 11/11；`v14-mutation-check` 8/8；`s2-pinkdot-check` 11/11；`interaction-test` **20/0**；s2 写回/合并 12/9；`data\notes.json` 全程只读。

**诚实边界**：Win11 任务栏是 XAML、不能枚举，"悬停真的画出缩略图"只能**人工看一眼**（验收 W3）；自动化钉死的是它的客观前提（按钮位、标题、未 cloaked、数量一致）。副作用（必然、非缺陷）：贴纸进入 Alt+Tab，`Win+D` 会把它们一起最小化。

## 增量 1.11（v17，2026-09-21）—— 点「贴到桌面」只贴点的那一张

用户原话：

> 「当桌面没有贴纸时,点击贴到桌面,会将所有贴纸都贴到桌面.改成仅贴点击了"贴到桌面"的那个贴纸」

**根因**：`StickerManager.Start()` 的 P1 兜底 `firstRunPlacement = _state.FirstRun || _records.Count == 0` ⇒ 空集时"贴出全部未归档便签"。而点按钮时贴纸通常没在跑（卡片刚被全关光），启动器**先写请求、再启动贴纸**（v13.1 顺序）⇒ 贴纸以空集启动、兜底触发、全部被贴，随后那条 place 变成 `AlreadyPlaced` 空操作（v14 用户现场日志就是这个形状）。

**修法（三处小改动，无新协议）**：①`StickerManager.SuppressPlaceAllForExplicitPlacements(firstRunPlacement, cliNoteCount, pendingBridgePlaces)` 纯谓词——**显式目标压制兜底**；②`DesktopBridge.PendingPlaceCount()` 数出 request.json 里 **seq > ack 的 `place`** 条数（已 ack 的 place、remove、`exit` 都不算）；③贴纸半在 `mgr.Start()` **之前** 调 `mgr.SetPendingBridgePlaces(bridge.PendingPlaceCount())`。用"有没有人点名"而不是"我被谁启动"——前者是事实，后者是不可靠的间接信号。

**语义边界（别误改）**：桌面空 + 点 1 张 ⇒ 只贴 1 张；连点 2 张 ⇒ 只贴 2 张；桌面空 + **没人点名**（启动恢复 / 直接 `--sticker`）⇒ **全部重贴**（v11 P1 / v15 A 档，未改）；state 里有记录 ⇒ 按记录恢复（未改）；`--note <id>` 仍压制兜底（未改）。

| 件 | 角色 | 本代身份 |
|---|---|---|
| `贴贴便签.exe` | **交付主程序** | 266240 B / `345238D8654CEC38AC8B32FAA875BF92DD1155DD31BDBD8267CF7E3C966A7B49` / 20:19:43.054 |
| `贴贴便签.build-manifest.json` | 该 exe ↔ 18 个源文件（漂移 0） | 3628 B / `8EF1F740…` |
| `dist\贴贴便签-安装包.exe` | 安装包（负载仍 **7 件**） | 1307648 B / `186D9F7F…` / 20:19:44.534 |
| `.tools\sticker_build\Sticker.exe` | **仅开发/验证用** | 163840 B / `ACFFDA9B…` |

**脚本变化**：新增 `.tools\v17-place-only-check.ps1`（C1 一点只贴一张 / C2 日志有 `first-run place-all suppressed` 且另两张 id 不出现 / C3 发布视图 1 条 / **C4 负对照**：空集无请求 ⇒ 3 张全贴）。`.tools\v16-mutation-check.ps1` **改名 `.tools\v17-mutation-check.ps1`** 并扩充：**M5 删掉 pending-place 调用点 ⇒ C1 红**（端到端调用点变异）、**M6 读取器恒返 0 ⇒ `v17-bridge-counts-pending-places` 红**；`v14-merge-check` M3 阈值 95→97。

**自测新增 2 条**：`v17-explicit-target-suppresses-place-all`（谓词五方向）、`v17-bridge-counts-pending-places`（读取器：2 place 中 1 已 ack + 1 remove 已 ack + 1 exit 待办 ⇒ 必须读回 1）⇒ 两半 **PASS=97 FAIL=0**。

**本代实测**：`v17-place-only-check` 4/4；`v17-mutation-check` 10/10；`v15-autostart-check` 6/6；`v16-taskbar-check` 5/5；`v14-merge-check` 17/17；`v13-launcher-api` 37/37；`bridge-e2e` 16/16；`v14-installer-check` 11/11；`v14-mutation-check` 8/8；`s2-pinkdot-check` 11/11；`interaction-test` **20/0**（含 I13「空集重启贴全部」这条负对照）；s2 写回/合并 12/9；`data\notes.json` 全程只读。

**⚠️ 被依赖的契约**：本修复依赖 v13.1 的"启动器**先写请求再启动贴纸**"顺序。若将来改成"先启动、后写请求"，修复会**静默失效**（贴纸启动时看不到待办请求 ⇒ 又全贴）——改那个顺序必须同时改本批判据。

## 增量 1.12（v18，2026-09-21）—— 去掉卡片右键菜单

用户原话：

> 「去除右键桌面贴纸的"退出贴纸"功能」

**改动（只有 `StickerForm.cs`，四处全删）**：卡片原本持有一个**只有一项**的右键菜单（"退出贴纸"，v11 §7.4 / P5）——字段 `_menu`、构造期 `BuildMenu()`、`OnMouseDown` 里的"右键 ⇒ `_menu.Show(...)`"、`Dispose` 里的释放。现在**右键在卡片上什么都不弹**（也不再开始拖动/拉伸）。注释里写明了这推翻了 v11 §7.4 / P5 的老口径。

**退出通道（本批之后的完整清单，别让用户退不出去）**：①**托盘菜单的「退出贴纸」**（应用内唯一显式退出入口，`tray-menu-lists-placed` 仍断言它在场）；②启动器窗口的 **× / 「退出」按钮**（v14 的 `action:"exit"` 控制请求）；③**关掉最后一张卡**（v11 3.9 自动退出）；④`Alt+F4` = 只关那一张（不是退出程序）。边界：桥/托盘不可用时（找不到 `notes.json`）贴纸半不贴任何卡片并立即干净退出，不存在"没菜单没托盘还驻留"的进程。

| 件 | 角色 | 本代身份 |
|---|---|---|
| `贴贴便签.exe` | **交付主程序** | 266240 B / `0F21665DED865FED9F74FED77795FA3B268BED44F9C50668D212A96A7949144A` / 20:36:48.153 |
| `贴贴便签.build-manifest.json` | 该 exe ↔ 18 个源文件（漂移 0） | 3628 B / `8A61CE91…` |
| `dist\贴贴便签-安装包.exe` | 安装包（负载仍 **7 件**） | 1307648 B / `B1F98217…` / 20:36:49.678 |
| `.tools\sticker_build\Sticker.exe` | **仅开发/验证用** | 163840 B / `2C1FEA8E…` |

**判据**：源码级 `v18-card-right-click-has-no-menu`（`StickerForm.cs` 里 `ContextMenuStrip` 与 `_menu` 各出现 **0** 次）+ 端到端 `.tools\v18-no-card-menu-check.ps1` D1–D3（对每张卡投递 `WM_CONTEXTMENU` + 右键按下/抬起 ⇒ **可见窗口数不变且没有可见的 `WS_EX_TOOLWINDOW` 窗口**、卡片原样还在、关启动器窗口两半都退）+ 变异 M7（把菜单挂回去 ⇒ D1 红、源码栅栏也红）。自测仍是 **PASS=97 FAIL=0**（删 1 条旧断言、加 1 条新断言）。

**⏸ 本批活体回归**：收尾时**用户的实例正在运行**（20:32 起）⇒ 所有需要贴纸互斥体的脚本按设计 **exit 4 退让**（绝不杀用户实例）；用户随后自己关掉，**13 套活体已全部补跑通过**：`v18-no-card-menu-check` 3/3、`v18-mutation-check` 12/12（含 M7）、`v17-place-only` 4/4、`v16-taskbar` 5/5、`v15-autostart` 6/6、`v14-merge` 17/17（SKIP=0）、`v13-launcher-api` 37/37、`bridge-e2e` 16/16、`v14-installer-check` 11/11、`v14-mutation` 8/8、`s2-pinkdot` 11/11、`s2-write-e2e` 12/12、`interaction-test` **20/0**。

## 增量 1.13（v19，2026-09-21）—— 删掉"双击手册文件"提示 + 智能检测贴纸进程

用户原话：

> 「点击贴到桌面后:有 7 条请求还没被贴纸程序处理 —— 双击 .tools\sticker_build\Sticker.exe（贴纸程序才会在桌面建卡）.**删除提示,并实现智能检测贴纸进程**」

**为什么那条提示必须删**：它是 v13 时代的开发者话术——当时贴纸是独立的 `.tools\sticker_build\Sticker.exe`；**v13.1** 起启动器会在 `place` 时自动拉起贴纸，**v14** 起贴纸并入主程序（`--sticker` 模式），磁盘上**根本没有**那个文件 ⇒ 这句话术指向不存在的文件、还把本该自动完成的事推给用户。

**改动**：
1. `demo/app.js` 三处旧话术全删（逐卡小字、标题旁全局提示、POST 回执里的 `notfound/failed` 建议），并清掉 v14 前才有的"控制台窗口"说法。
2. **智能检测（后端）**：`GET /api/desktop` 新增 `stickerRunning`（以贴纸单实例互斥体为准，不靠进程名猜）与 `pendingRequests`（request.json 里 `seq > ackSeq` 的条数）；**轮询时若发现"有未处理的 place 且贴纸没在跑"，启动器自己把它拉起来**（沿用三道护栏：互斥体判在跑、8 秒节流、`TIETIE_NO_AUTOSTART=1` 一刀切）。日志只在真的尝试时写一行，不刷屏。
3. 网页用真实状态说话：在跑 ⇒「贴纸程序正在处理 N 条请求…」；没在跑 ⇒「已自动尝试启动（还有 N 条待处理）」；真失败 ⇒「自动启动贴纸程序失败（详见「贴贴便签」窗口里的日志）」——**不再出现任何文件路径，也不再要求用户手动做任何事**。

| 件 | 角色 | 本代身份 |
|---|---|---|
| `贴贴便签.exe` | **交付主程序** | 267776 B / `448C1CCE101A09FA2E5D05E318BA324D08001BA30E00FDA242A1A1585A71D090` / 20:52:01.521 |
| `贴贴便签.build-manifest.json` | 该 exe ↔ 18 个源文件（漂移 0） | 3628 B / `ADB0F4AA…` |
| `dist\贴贴便签-安装包.exe` | 安装包（负载仍 **7 件**，含新的 `demo/app.js`） | 1315328 B / `8BEC985C…` / 20:52:39.158 |
| `demo\app.js` | 前端（改了话术与状态字段；**不在 exe 的 manifest 里，改它一定要重打安装包**） | 31298 B / `5221834E…` / 20:51:42.616 |
| `.tools\sticker_build\Sticker.exe` | **仅开发/验证用** | 164864 B / `3814F672…` |

**判据**：源码级 `v19-no-stale-sticker-hint`（读**随包发布**的 `demo/app.js`：`sticker_build` / `Sticker.exe` / `控制台` 各 0 次）+ 端到端 `.tools\v19-smart-detect-check.ps1` E1–E4（E1 一次 GET 报出 `stickerRunning=false` / `pendingRequests=2`；**E2 负对照**：带 `TIETIE_NO_AUTOSTART=1` 时轮询不会擅自拉起贴纸；E3 不带开关时一次 GET 就把贴纸拉起来；E4 拉起来的半把队列消费成 2 张卡、`pendingRequests→0`）+ 变异 **M8**（旧提示写回 ⇒ 页面栅栏红）、**M9**（自愈永假 ⇒ E3 红）。自测 **98/0**。

**⏸→✅ 本批活体回归**：收尾时**用户实例又在运行**（pid 4300，20:48:52 起）⇒ 脚本照例 exit 4 退让；用户关掉后**已全部补跑通过**：`v19-smart-detect-check` **4/4**、`v19-mutation-check` **14/14**（含 M8/M9）、`v18-no-card-menu` 3/3、`v17-place-only` 4/4、`v16-taskbar` 5/5、`v15-autostart` 6/6、`v14-merge` 17/17（SKIP=0）、`v13-launcher-api` 37/37、`bridge-e2e` 16/16、`v14-installer-check` 11/11、`v14-mutation` 8/8、`s2-pinkdot` 11/11、`s2-write-e2e` 12/12、`interaction-test` **20/0**。⚠️ 补跑中发现 E3 的一条漏洞：它当时没关 v15 的启动恢复，于是"启动器启动时就拉起贴纸"替自愈顶包、**M9 变异逃掉了**；加上 `TIETIE_NO_STARTUP_STICKER=1` 并断言"poll 前贴纸数=0"后 E3 才真正在测自愈。

## 增量 1.14（v20，2026-09-21）—— 开机自启动（可在「贴贴便签.exe」窗口设置）

用户原话：

> 「添加开机自启动功能(可在贴贴便签.exe窗口设置)」

**机制**：往 `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` 写一条字符串值——名字 = **`贴贴便签`**，数据 = **`"<本 exe 全路径>" --no-open`**。**不需要管理员权限**（与安装包/程序一致的既有约束）、不用任何第三方库（`.lnk` 要走 COM/P/Invoke，任务计划程序更重且部分选项要管理员）。窗口底部新增复选框「**开机自动启动（自动恢复桌面便签）**」，默认**不勾**；勾选态**从注册表读回来**，写失败时勾选框自己弹回去。

**两个"绝不能"**：①`--no-open` 必须在（否则每次登录都弹一个浏览器标签）；②**绝不能有 `--headless`**——它会在"启动恢复桌面贴纸"之前就 `return`，那样开机后桌面上**根本不会有便签**。

**状态三态**：`on`（那条值逐字等于本程序设置）/ `off`（没有）/ **`stale`（有一条但不是本程序设置，例如旧安装路径 ⇒ 显示未勾选，并在日志里说明它指向哪——绝不假装"已开启"）**。

**自动化入口**：`贴贴便签.exe --autostart-on|--autostart-off|--autostart-status`（与窗口复选框**共用同一套 `AutoStart`**，所以脚本驱动的是产品的真实接线）；环境变量 `TIETIE_AUTOSTART_ROOT` 把读写**重定向到测试子键**，验证脚本只碰测试键（真实启动项全程只读，并由 E7 断言前后逐字不变）。另有护栏：**写真实键时拒绝 `.tools\` 下的路径**（变异体/沙盒副本不许进真实启动项；重定向时不受限）。

| 件 | 角色 | 本代身份 |
|---|---|---|
| `贴贴便签.exe` | **交付主程序** | 274944 B / `FD594653800C2A2E85F486D4940D4135E7273A26D943F96B4785586C4F232F67` / 21:15:32.202 |
| `贴贴便签.build-manifest.json` | 该 exe ↔ 18 个源文件（漂移 0） | 3628 B / `70C62853…` |
| `dist\贴贴便签-安装包.exe` | 安装包（负载 **7 件 / 463476 B**） | 1334272 B / `AA493331…` / 21:18:29.954 |
| `demo\app.js` | 前端（**本批未改**） | 31298 B / `5221834E…` / 20:51:42.616 |
| `.tools\sticker_build\Sticker.exe` | **仅开发/验证用** | 168448 B / `DDA735DC…` |

**判据**：源码栅栏三条（`v20-autostart-checkbox-is-wired`：恰好一个 `CheckBox` + 有 `CheckedChanged +=` 接线 + 处理里真调 `Enable/Disable`；`v20-autostart-box-reflects-the-registry`：勾选态读自注册表；`v20-autostart-single-writer-and-boot-flags`：Run 子键只有一个命名处 + 命令行带 `" --no-open"`）+ 端到端 `.tools\v20-autostart-check.ps1` **E0–E7 8/8**（**E5 起真窗口、按文字找到复选框、用 `BM_CLICK` 点两次**，每次都要让注册表值出现/消失，窗口日志依次出现未开启/已开启/未开启；E6 `.tools` 副本写真实键被拒；**E7 真实启动项前后逐字不变**）+ 变异 **M10/M11/M12**。自测 **101/0**。

**⏸→✅ 本批无退让**：一次跑完 16 套（`.tools\_v20-sweep.ps1`，150 秒，互斥体空闲、SKIP=0）全绿：`v20-autostart-check` **8/8**、`v20-mutation-check` **18/18**（含 M10/M11/M12）、`v19-smart-detect` 4/4、`v18-no-card-menu` 3/3、`v17-place-only` 4/4、`v16-taskbar` 5/5、`v15-autostart` 6/6、`v14-merge` 17/17、`v13-launcher-api` 37/37、`bridge-e2e` 16/16、`v14-installer-check` 11/11、`s2-pinkdot` 11/11、`s2-write-e2e` 12/12、`interaction-test` **20/0**、S2 合并 9/9。

⚠️ **本批自查里的真实教训（写在这里给以后复用）**：`v20-autostart-check.ps1` 的第一版**只在 E5 里设了重定向**，E0–E4 于是驱动产品写进了**真实** Run 键（**E7 当场变红抓到它**）⇒ 已即时删除那条值并复测（用户机器上的启动项恢复为空），脚本改成**在整场检查最外层设置重定向**。教训：**重定向必须在覆盖全部用例的那一层设置**；而"前后逐字相同"这类不污染断言确实有分辨力。
⚠️ **UIA 事实**：这个窗口里的控件在 UI Automation 下**全是 `ControlType.Pane`、没有任何可用 pattern**（只有 MSAA 桥）⇒ 不能按 `ControlType.CheckBox` 找、也不能用 `TogglePattern`；正确做法是**按文字找控件 + `BM_CLICK`(0x00F5)**。

**本期不做**：安装时不自动开启（默认关）；**卸载时不清理由此产生的启动项**（卸载前先取消勾选即可）；不做"只启动贴纸、不开窗口"的静默变体。**已知边界**：启动器没有单实例保护 ⇒ 勾了自启动又手动双击会开两个窗口（既有待办）。
## 增量 1.15（v21，2026-09-22）—— 关掉卡片＝记住摆放；桌面空了按你自己的摆放放回来

用户原话：

> 「开机自启动，会导致所有便签都被贴到桌面，且都是初始瀑布式排布，不是之前的样式」

**根因（用户日志里的三行，不是猜测）**：v11 定的"找回规则"是「重启时若放置集合为空，就按首启规则贴出全部」；而当时"关闭一张卡"会把它的记录（连同坐标）**删掉**，所以「把卡逐张关光」等于把摆放存档清成空集。以前这条规则只在**用户手动打开程序**时才触发，所以看起来是好事；**v20 加了开机自启之后，它变成每次登录都执行** ⇒ 用户 09-22 08:16 与 14:13 两次开机都被"全部 9 张便签按 40,40/68,68/96,96… 瀑布铺满"。（对照：存档里有记录时，程序一直能按原坐标还原——旧日志 09-21 21:08:04 就是如此。）

**改动（三个源文件）**：
1. `State.cs`：记录新增 `closed` 字段（**只写 true**，位置/尺寸/显示器全留着）。
2. `StickerManager.cs`：
   - 关闭一张卡（❌ / Alt+F4 / 网页「从桌面收起」）改为 `CloseRecord`——**标记已收起而不是删记录**；
   - 启动时新增纯函数 `ShouldReopenClosedSet(openCount, closedCount, cliNoteCount, pendingBridgePlaces)`：**桌面为空 + 有记忆 + 无人点名** ⇒ 把记住的那几张按**原位置**放回来；
   - "贴出全部未归档便签（瀑布位）"只剩**真正首次运行 / 存档被丢弃**；
   - `PlacedNoteIdsOrdered()` / `BridgePlacements()` 跳过已收起的记录 ⇒ 网页按钮、托盘清单、`placed.json` 只跟在**桌面上的**一致；
   - 网页收起后再点那张的「贴到桌面」⇒ 回到它记住的位置（`bridge-reopen`）。
3. `SelfTest.cs`：5 条新断言 + 2 条旧断言改口径（v13 的 `bridge-place-idempotent` / `bridge-unknown-note-advances` 现在数"开放/记住"两个数）。`interaction-test.ps1` 的 I7/I10/I11/I12/I17/I19 也跟着改成"记录保留 + closed:true + 在桌卡片数"，I8 的收敛赢家改为"第一个未收起的记录"。

| 件 | 角色 | 本代身份 |
|---|---|---|
| `贴贴便签.exe` | **交付主程序** | 283136 B / `F560A445EB67046997ADFFB7436AED31B4EFD4E64C4436E8AAA617FF4FA7E403` / 19:07:46.259 |
| `贴贴便签.build-manifest.json` | 该 exe ↔ 18 个源文件（漂移 0） | 3628 B / `0E8C8054…` |
| `dist\贴贴便签-安装包.exe` | 安装包（负载 **7 件 / 471668 B**） | 1356288 B / `0E79B0EF…` / 19:19:28.973 |
| `.tools\sticker_build\Sticker.exe` | **仅开发/验证用** | 176640 B / `1DB342F0…` |

**判据**：白盒 5 条 + 活体 `.tools\v21-closed-set-check.ps1` **F1–F4**（F1 = 空桌面 + 2 张记忆 ⇒ 只放回这 2 张、坐标 300,180 300x200 与 520,260、日志出现 `v21 reopen` 且**没有** `first-run placement`；F2 负对照没存档⇒仍贴全部；F3 空桌面 + 一个待处理点击 ⇒ 只开那一张、其余保持收起、日志 `v21 reopen suppressed`；F4 关闭一张⇒几何保留 + 发布视图只列在桌的）+ 变异 **M13**（还原步骤不可达 ⇒ F1 红）/ **M14**（关闭改回删除 ⇒ 白盒红）。自测 **106/0**。

**✅ 全绿无退让**：`.tools\_v21-sweep.ps1` 17 套一次跑完（155 秒，互斥体空闲、SKIP=0）；`.tools\v21-mutation-check.ps1` **20/20**（control 14/14 护栏在场 + M1–M14）。

⚠️ **测试侧的一个坑**：`.tools\v21-closed-set-check.ps1` 第一版用"日志里出现 `state written (startup)`"当启动完成信号，而贴纸**一直开着 `sticker-debug.log`** ⇒ `Remove-Item` 删不掉它、旧日志里那行让等待提前返回，F3/F4 读到了旧存档（**产品是对的、脚本是错的**）。改成**等新进程应当产出的那份状态**（轮询存档里的开/收记录数）后全绿。

**已知边界**：①记忆会累积（每条约 100 字节，只有该便签从 `notes.json` 消失时才丢）；②"关光了"之后回来的是"全部记住过的"，不是"最后一次那几张"（上月关掉的也会跟着回来，只要还在 `notes.json` 里且未归档）。
## 增量 1.16（v22，2026-09-29）—— 装完不再有"测试用的便签残余"

用户原话：

> 「安装包安装后,会有测试用的便签残余」

**根因（file:line）**：`demo/app.js:12-33` 带着 `const SEED`（10 条示例便签 `seed-1..seed-10`）+ `SEED_FLAG`，`:611-617` 在 `boot()` 里判"库为空且从未播种"就 `store.saveMany(seeds)` ⇒ 装完第一次打开页面，**示例便签被写进用户的 `data\notes.json`**。启动器从不播种（`NoteStore` 只会在文件缺失时写 `[]`）⇒ 这是页面写进用户数据的，服务端检查拦不到。

**改动**：删掉种子数组、播种块、`SEED_FLAG` 两处写入；空库保持空（页面已有「🗒️ 这里还没有便签」空状态）。**只改前端**：`demo/app.js` 31298 → **28336 B**；`exe`/`manifest` **未重建**（哈希与 v21 逐位相同）；**重打安装包**。

| 件 | 角色 | 本代身份 |
|---|---|---|
| `demo\app.js` | 前端（本批修改） | **28336 B / `14A0639FD7145EE6E3DCE9D01BB7D3BCB85D54CF71A21CBAC0296253320F0ADB` / 2026-09-29 00:19:53.186** |
| `dist\贴贴便签-安装包.exe` | 安装包（重打；负载 7 件 / 468706 B） | **1348096 B / `D62C254C6FABC84A595A270C4946D7CBA795F08529DA2B677FE78C36DA93398A` / 00:20:25.810** |
| `贴贴便签.exe` | 交付主程序（**本批未重建**） | 283136 B / `F560A445…` / 2026-09-22 19:07:46 |

**判据**：`.tools\v22-blank-install-check.ps1` **G1–G4 4/4**（G1 全新沙盒启动器 ⇒ `GET /api/notes=[]` 且它建出的数据文件仍是 `[]`；G2 随包页面栅栏：`"seed-`/`const SEED`/`SEED_FLAG`/`saveMany(seeds)` 各 0、空状态文案 ≥1；**G3 解包安装包核对 `demo/app.js` 与仓库逐位相同**——专拦"改了前端忘了重打安装包"；G4 同一条栅栏对"把种子写回去的拷贝"必须判 DIRTY）+ 变异 **M15**。

**✅ 全绿**：`.tools\v22-mutation-check.ps1` **21/21**（control 14/14 护栏在场 + M1–M15）；`.tools\_v22-sweep.ps1` **18 套**：17 套一次通过（含两半 `--selftest` 106/0、v14-installer 11/11），`interaction-test` 首跑 I1 合成拖动落点环境性抖动 1 例（窗口与存档一致地移到 201,125，期望 160,120）⇒ **立刻重跑 20/0**。

**已知残留**：①已装过的环境里那 10 条**不会被自动删**（已在用户数据文件里）——要到「归档」页逐条删，或经用户授权后由我只删 `id` 以 `seed-` 开头的条目（先备份）；②`demo/demo-standalone.html`（不写文件的离线演示页，界面无入口）里仍有硬编码示例数据，本批不动；③浏览器→文件的一次性迁移仍开着（那是搬用户自己的旧数据）。
## 增量 1.17（v23，2026-09-29）—— 启动器窗口改成网页风格（方案 B · 全网页化 + 启动器半 DPI 感知）

用户原话：

> 「能否把贴贴便签.exe的控制界面改成和网页相似的风格(可以的话先生成测试demo)」

**做法**：先出三档候选外观的**可双击预览**（`.tools\v23-style-demo\`，含三张 1:1 渲染 PNG；预览程序与正式实现**共用同一套自绘代码**，`PaintB` 就是规格示意），用户拍板 **方案 B**（无边框圆角窗 + 自绘标题栏）并选择**顺手把启动器半改成 DPI 感知**。

**改动**：`launcher/LauncherWindow.cs` 整体重写（29109 B）——自绘标题栏（品牌图钉 + 最小化/关闭 + 按住拖动）、地址胶囊（真 TextBox 打在自绘胶囊里）、渐变主按钮、日志卡片（真 TextBox）、状态胶囊、整抄 `demo/styles.css` `:root` 的网页调色板；`launcher/Program.cs` +15 行（启动器分支声明 `SetProcessDPIAware()`，**语句位置在贴纸派发之后**——贴纸半是另一个进程、保持不感知，`sticker-state.json` 里那些 unaware 像素坐标不受影响）。**真控件一个没丢**：开机自启动**仍是真 CheckBox**（v20 的 E5 靠 UIA 找它 + `BM_CLICK` 驱动；换自绘控件那条判据就失去驱动点）。

| 件 | 角色 | 本代身份 |
|---|---|---|
| `贴贴便签.exe` | 交付主程序（**本批重建**） | **293888 B / `95A4C08680DB52C6A1090435C8EBCB67A8653678F74BD0417B26D4FCC12C3519` / 2026-09-29 00:54:43.968** |
| `贴贴便签.build-manifest.json` | 该 exe ↔ 18 个源文件（漂移 0） | **3629 B / `E6681FEC…`** / 00:54:44.156 |
| `dist\贴贴便签-安装包.exe` | 安装包（重打；负载 **7 件 / 479458 B**） | **1376768 B / `11ED28AB…` / 01:00:24.777** |
| `.tools\sticker_build\Sticker.exe` | **仅开发/验证用**（本批未重建：贴纸半一行未改） | 176640 B / `1DB342F0…` / 2026-09-22 19:07:07 |

**判据**：`.tools\v23-launcher-style-check.ps1` **V0–V7 15/15**（V1 仍是正常任务栏应用；V2 DPI 感知的**真实**尺寸 = 520×336 × 窗口dpi/96 = 650×420；V3 圆角 `Region`；V4 五点网页配色；V5 真控件仍在场（日志/地址 EDIT、自启动 CHECKBOX、四个自绘按钮）；V6 点自绘最小化真的最小化；V7 点自绘关闭真的退出）+ 变异 **M16–M20**（去 DPI / 改底色 / 去圆角 / 断关闭 / 断最小化，各自让对应判据变红）。自测两半仍 **106/0**。

**⚠️ 本批踩到三个"判据工具本身的坑"**（写进规格 §4.3）：①**BOM-less `.ps1` 里的中文注释会吞掉下一行**——PS 5.1 按 GBK 读，汉字尾字节把行尾 `\r` 当自己的第二字节 ⇒ 换行消失、下一行整行并进注释，三个变量静默变 `$null`（判据打出 `open(len=0)`）；已给检查脚本加 **ASCII 纯度自检**；②**跨进程读不到多行 EDIT 的文字**（`GetWindowText` 返回空串，必须 `SendMessage(WM_GETTEXT)`；实测 0 vs 114）；③**不感知 DPI 的探针看到的是虚拟化坐标**（同一个 650×420 窗口读成 520×336、`PrintWindow` 只截到左上角）。

**✅ 全绿**：`.tools\v23-mutation-check.ps1` **27/27**（control 14/14 护栏在场 + M1–M20）；`.tools\_v23-sweep.ps1` **19 套一次跑完 171 秒、互斥体空闲 SKIP=0**（含两半 selftest 106/0、新套件 v23 15/15、v20-autostart 8/8 ← **E5 仍能驱动真复选框**、v14-installer 11/11、活体 interaction-test 20/0）。

**已知残留**：①复选框仍是 Windows 原生方块（**故意**：判据理由）；②CSS 毛玻璃 / 浏览器级字体渲染 / 大范围柔光阴影**抄不到** ⇒ 是"风格高度接近"；③无边框窗没有系统标题栏菜单，**拖动只能人工验收**（合成拖动会进 Windows 的模态拖动循环、把验证脚本挂死）；④改前的 `LauncherWindow.cs` **未单独备份**（回滚靠 `.tools\v23-prev\` 里的 v21/v22 主程序 + 旧 manifest）。
## 增量 1.18（v24，2026-09-29）—— 换应用图标（抠出卡通图钉 → 7 条目多尺寸 ico）

用户原话：

> 「能否根据这张图,扣取高清的卡通图钉图片,制作好后先给我确认(要用于软件的图标)」

**顺序**：先只出图 + 对照图给他确认（**没先动程序**）→ 他拍板「只放图钉（透明底）」+「就用这版忠实抠图」→ 才落进程序。

**抠图**（`.tools\_icon_pin_hd\`，开发件）：渐变底用"卡片内侧 1..6px 边框环的平面拟合"建模（训练残差均值 0.50），按残差取 alpha ⇒ 粉钉帽 / 深红褶皱 / 灰色针都保住。**三个坑**：背景模型取卡片角块会取到圆角外的纸色（全图变图钉）、取中心会学到图钉本身；阈值必须取高以切掉图钉自带的柔和投影（否则放大是一圈灰雾）；低透明度处不要反解颜色（减掉暖底会把边缘染蓝）。内孔强制不透明（否则高光半透明、整枚发白）。

| 件 | 角色 | 本代身份 |
|---|---|---|
| `launcher\app.ico`（= `.tools\_icon\app.ico`） | 图标（**7 条目** 16/24/32/48/64/128/256） | **141212 B**（旧：78537 B 单条 256） |
| `贴贴便签.exe` | 交付主程序（**重建**：只换图标资源，源码一行未改） | **356864 B / `336E78B6…` / 2026-09-29 15:26:03.892** |
| `dist\贴贴便签-安装包.exe` | 安装包（重打；负载 7 件 / 605410 B，`卸载.exe` 也换图标） | **1775616 B** |
| `.tools\_icon\_icon_source_v2.png` | **图标作者源**（25×24 透明；`_icon_source_v2_tile.png` = 用户原图存档） | 909 B / 4516 B |
| `.tools\sticker_build\Sticker.exe` | 仅开发/验证用（未重建） | 176640 B / `1DB342F0…` |

**复验**：**本批不加自动化图标判据**（既有脚本没有一条读图标像素；硬比字节拦不住"图标画错"）⇒ 改走 ①`ExtractAssociatedIcon()` 从产出的 exe/安装包**回读真图**亲眼看；②`.tools\_v23-sweep.ps1` **19 套对重建后的 exe 重跑 19/19 全绿**。

**已知残留**：①源图里图钉本体仅 **25×24 像素** ⇒ 16–48px 清晰、**128/256px 偏软**（忠实抠图的物理上限）；②图钉自带的柔和投影被去掉了（透明图标不该烘焙阴影）；③**Windows 图标缓存**：覆盖安装后旧图标可能仍显示，需重启资源管理器或注销一次；④旧图标资产未删，回滚用 `.tools\v24-prev\`。

---

## 增量 1.19（v25，2026-09-29）—— 网页版变成"能装的应用"（PWA），为手机做准备

用户原话：

> 「现在的网页应用能否移植到手机上?先说可能性」

**用户拍板**：①路线「A：先做个网页版 App（PWA）（推荐，最省）」②手机图标「A：渐变底 + 图钉（推荐，已落地）」③分发「先只验收桌面这版，手机以后再说（推荐）」。

**决定性前提**（用户 2026-09-06 早已拍板）：「手机端和电脑端不需要互通,保障数据存储格式一致,可以快速导出导入数据就行」⇒ **不做同步 / 服务器 / 账号**；手机是**独立一份数据**（存手机浏览器自带数据库），交换数据用界面**已有的导出/导入按钮**。

**本批只动前端与安装包**：

| 件 | 角色 | 本代身份 |
|---|---|---|
| `demo/manifest.json` | （新）web app manifest：standalone、`start_url`/`scope` 用相对路径 | 779 B |
| `demo/sw.js` | （新）Service Worker：预缓存 11 项、缓存优先+后台刷新、**`/api/` 绝不缓存**、离线导航退回首屏 | 2832 B |
| `demo/icons/*.png` | （新）手机图标 5 件（192/512 any + 192/512 maskable + 180 apple-touch） | 共 264235 B |
| `installer/make-pwa-icons.ps1` | （新）手机图标生成器（纯 ASCII，`-Flavour tile\|pin`） | — |
| `demo/index.html` | manifest link + apple-touch-icon + 3 个 iOS meta + SW 注册（`file://` 下静默跳过） | 5580 B |
| `installer/build-installer.ps1` | 负载清单 **7 → 14**（`FileList.cs` 由它生成，卸载清单随之同步） | — |
| `installer/Uninstall.cs` | 补删 `demo\icons` 空目录，否则残留空壳 | — |
| `贴贴便签.exe` | **未重建**（启动器 MIME 表本来就有 `.png`，manifest 走 `.json`） | 356864 B / `336E78B6…` |
| `dist\贴贴便签-安装包.exe` | 重打；负载 **14 件 / 874789 B** | **2494464 B / `46003438D610976E…`** |

**手机图标为什么不用 exe 那版（不是反复）**：iOS 把 apple-touch-icon 合成到**黑底**（透明底会变成黑方块）；安卓 maskable 要求满幅底色、内容须在中心 80% 圆内；而用户那张原图本来就是"渐变方块 + 图钉"的方形图标设计。做法：渐变**不放大 55×65 位图**（放大 9 倍必糊），而是**采样四角颜色重画双线性渐变**（`#FFC381` / `#FFAE91` / `#FFAD92` / `#FF98A3`），图钉复用 v24 那枚抠图；maskable 版把图钉缩到 0.46（半对角 ≈0.33 < 0.40，稳在安全圆内）。

**顺手修掉一个真 bug**：`installer\make-icon.ps1` 的 Lanczos **放大分支是错的**（`support = 3*scale`）。它自 v24 起**只被用于缩小**（25×24 → 16/24/32/…），所以从未暴露；v25 要把源图放大 7–11 倍，第一次走这条路 ⇒ **每个输出像素都在平均整张 25×24 源图、图钉被完全抹平**。新生成器改用标准 filter scaling（放大时核保持 3 **源**像素宽）。**没有去动 `make-icon.ps1`**（v24 已验证的生成器）。

**验证**：三层共 45 项 —— 静态（脚本非 ASCII **0**、manifest 合法、`node --check sw.js` 通过、5 个都是真 PNG）+ `.tools\_pwa-http-check.ps1` **14/14** + `.tools\_pwa-browser-check.ps1` **12/12**（真 Edge 154 headless + 独立 profile：真注册、11 条缓存、**无 `/api/`**、**停掉服务后仍能 dump 出完整页面**）+ `.tools\_v23-sweep.ps1` **19 套全绿 / 167 s / SKIP=0**。

**怎么用**（桌面已经就是完整版）：打开启动器 → 浏览器地址栏右侧的"安装"图标 → 装成一个独立窗口的「贴贴便签」（无地址栏）→ **断网也能打开**。

**已知残留 / 诚实边界**：①**未在真机验证**（手边没有手机）②**手机要拿到含离线的完整 PWA，页面必须从 https 打开**——浏览器只在安全上下文（https / `localhost` / `127.0.0.1`）允许注册 SW；手机若走"同一 Wi-Fi 连电脑 `192.168.x.x`"是明文 http ⇒ 能打开能用、iPhone 还能加主屏全屏，但**没有离线**、安卓不弹安装提示 ③iOS 可能清理长期不访问的站点数据 ④512 图标软度是 25×24 源的物理上限 ⑤PWA 未纳入自动化套件（两个探针是 `_` 前缀 dev-probe）⑥**桌面上的 PWA 若脱离启动器打开（启动器没在跑），`store.js` 会回退到浏览器本地库 ⇒ 界面上可能是空的**（数据仍在 `data\notes.json`，启动器一开就回来）；要不要在这种状态下给界面加一句提示，留给用户定。

---

## 增量 1.20（v26，2026-09-29）—— 「打开便签界面」优先打开**已安装的应用**，不再弹浏览器网址

用户原话：

> 「可以了,但是打开便签页面打开的还是网址，而不是下载的应用(电脑端)」

**根因**：`launcher/Program.cs` 原本那一行是

```csharp
Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
```

`UseShellExecute` + URL = **交给系统默认浏览器开一个标签页**。而他已经在 Chrome 里把网页版装成了 PWA —— 那是**另一条完全独立的入口**（`chrome_proxy.exe --profile-directory=Default --app-id=gdiemljfmnfogemddapdnihghiddfebn`）。于是「应用确实装好了」与「点按钮出来的是网址」同时为真，他有充分理由认为坏了。

**新规则（顺序固定）**：
1. **优先**：在搜索根里找"已安装应用"的快捷方式，**必须同时满足**两条 —— a) 名字以 `贴贴便签` 开头（系统遇到重名会变成 `贴贴便签 (1)` ⇒ 前缀匹配）；b) 命令行里含 `--app-id=`。
2. **找不到才退回**原来的行为：让默认浏览器打开网址。

**第 b 条是命门**：搜索根里另有一个**同名**快捷方式指向**本 exe 自己**（启动器），只看名字会把它选中 ⇒ "点一下又开一个启动器"，比原来的毛病更糟。判据专门为它写了负对照。

| 件 | 角色 | 本代身份 |
|---|---|---|
| `launcher/Program.cs` | 新增 `static class AppLaunch` + 只读命令行口 `--print-open-target` | 79880 B |
| `launcher/LauncherWindow.cs` | `OpenBrowser()` 改走 `AppLaunch.Open`，结果写进日志区 | 29281 B |
| `贴贴便签.exe` | 交付主程序（**重建**） | **358912 B / `A149B141…` / 2026-09-29 17:26:29.235**（旧 356864） |
| `dist\贴贴便签-安装包.exe` | 安装包（重打；负载 14 件 / 876837 B） | **2500096 B / `B551612D…` / 17:26:44.529**（旧 2494464） |

**可测性**：`--print-open-target` 只回答"这次会开什么"（`app|<lnk>` 或 `url|<url>`），不起服务、不建窗口、不碰状态；`TIETIE_APP_SEARCH_ROOTS` 把搜索根指向沙盒 ⇒ 判据驱动的是**真实程序里的选择逻辑**，真实快捷方式全程只读。

**判据**：`.tools\_v26-open-app-check.ps1` **7/7** —— A1 正例 / **A2 同名但指向本 exe ⇒ 必须走 url（负对照 #1）** / **A3 别的应用 ⇒ 必须走 url（负对照 #2）** / A4 空 ⇒ 回退 / **A5 诱饵排在前面也必须跳过** / A6 根不存在不炸 / **A7 167 个真实快捷方式逐字节不变（不污染）**。既有回归 `.tools\_v23-sweep.ps1` **19 套全绿 / 162 s / SKIP=0**。

**本批踩的坑**：**winexe + 重定向 stdout + 非 ASCII = 乱码**。`--print-open-target` 的输出含中文路径，第一次跑 A1/A5 假红（`got: app|D:\work\AI??Ŀ\...`）。根因：`/target:winexe` 下 `Console.OutputEncoding = UTF8` **在重定向场景不一定生效**（setter 抛异常被吞），按系统代码页写出、按 UTF-8 读入 ⇒ 成片 U+FFFD。**此前所有 stdout 输出都是 ASCII，所以这坑从没露过面。**修法 = 显式写 UTF-8 字节（`Encoding.UTF8.GetBytes` + `Console.OpenStandardOutput()`）。

**已知残留 / 诚实边界**：①**必须覆盖安装这一版**才生效（他 17:11 装的 2494464 B 那份是 v25）②**应用 ID 绑定 `127.0.0.1:8787`**：若该端口被占、启动器自动换端口，已装应用的图标会指向旧端口（表现为空白）③选择只按"名字前缀 + `--app-id=`"，若他把快捷方式改名成不含 `贴贴便签` 的名字 ⇒ 退回开网址（可接受的降级）④**找不到应用时行为与旧版完全一致**，本批不会让事情变得更差 ⑤系统默认浏览器登记是 **Edge**，而他日常用 **Chrome** —— 启动器"退回开网址"时开的是 Edge。