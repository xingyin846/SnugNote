const base = 'http://127.0.0.1:8090/';

async function main() {
  const [html, js, css] = await Promise.all([
    fetch(base).then(r => r.ok ? r.text() : Promise.reject(`index.html HTTP ${r.status}`)),
    fetch(base + 'app.js').then(r => r.ok ? r.text() : Promise.reject(`app.js HTTP ${r.status}`)),
    fetch(base + 'styles.css').then(r => r.ok ? r.text() : Promise.reject(`styles.css HTTP ${r.status}`)),
  ]);
  console.log('资源加载: index.html / app.js / styles.css 全部 OK');

  // 提取 app.js 中引用的 id
  const ids = new Set();
  const re = /\$\("#([A-Za-z0-9_-]+)"\)|getElementById\("([A-Za-z0-9_-]+)"\)|querySelector\("#([A-Za-z0-9_-]+)"\)/g;
  let m;
  while ((m = re.exec(js))) for (let i = 1; i < 4; i++) if (m[i]) ids.add(m[i]);
  console.log('JS 引用 #id 数量:', ids.size);

  const missing = [...ids].filter(id => !html.includes(`id="${id}"`));
  if (missing.length) { console.log('❌ 缺失 id:', missing.join(', ')); process.exitCode = 1; }
  else console.log('✅ JS 引用的全部 id 在 HTML 中都存在');

  // 核心交互元素
  const keys = ['board','searchInput','modalMask','tagList','themeBtn','newBtn','newBtnMobile','cancelBtn','saveBtn','colorPicker','toast'];
  const missKey = keys.filter(k => !html.includes(`id="${k}"`));
  console.log(missKey.length ? `❌ 缺少关键元素: ${missKey.join(', ')}` : '✅ 关键交互元素齐全');
}
main().catch(e => { console.error('❌', e); process.exit(1); });
