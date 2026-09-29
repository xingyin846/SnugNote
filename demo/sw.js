/* 贴贴便签 service worker —— 让网页版能"装"到手机主屏幕、断网也能打开。
 *
 * 两条硬规矩：
 *   1) /api/ 一律直连，绝不缓存。那是便签数据本身（读用它、勾选回写也用它），缓存了会让
 *      界面拿旧数据显示、或者把一次写入失败伪装成成功——这比"打不开"严重得多。
 *   2) 只接管同源 GET。非 GET（保存便签）一律放行走网络。
 *
 * 静态资源用"缓存优先、后台刷新"：打开够快，同时下次打开能拿到新版本。
 * 想强制换代就改下面的 CACHE 版本号，旧的缓存在 activate 里被清掉。
 */
const CACHE = 'tietie-v1';

/* 首次安装时预抓这些。"逐个 add 并吞掉失败"是故意的：托管方少一个文件时，
   不该让整个安装失败、连离线打开都一起废掉。 */
const PRECACHE = [
  './',
  './index.html',
  './styles.css',
  './app.js',
  './store.js',
  './manifest.json',
  './icons/icon-192.png',
  './icons/icon-512.png',
  './icons/icon-maskable-192.png',
  './icons/icon-maskable-512.png',
  './icons/apple-touch-icon.png'
];

self.addEventListener('install', (e) => {
  e.waitUntil((async () => {
    const cache = await caches.open(CACHE);
    await Promise.all(PRECACHE.map((u) => cache.add(u).catch(() => {})));
    await self.skipWaiting();
  })());
});

self.addEventListener('activate', (e) => {
  e.waitUntil((async () => {
    const keys = await caches.keys();
    await Promise.all(keys.filter((k) => k !== CACHE).map((k) => caches.delete(k)));
    await self.clients.claim();
  })());
});

self.addEventListener('fetch', (e) => {
  const req = e.request;
  if (req.method !== 'GET') return;                  // 写操作直连
  const url = new URL(req.url);
  if (url.origin !== self.location.origin) return;   // 不碰别的站
  if (url.pathname.startsWith('/api/')) return;      // 数据必须实时，见文件头

  e.respondWith((async () => {
    const cache = await caches.open(CACHE);
    const hit = await cache.match(req, { ignoreSearch: true });
    const net = fetch(req).then((res) => {
      if (res && res.ok && res.type === 'basic') cache.put(req, res.clone());
      return res;
    }).catch(() => null);

    if (hit) {
      net.catch(() => {});        // 后台刷新，失败无所谓（离线就是没得刷）
      return hit;
    }
    const res = await net;
    if (res) return res;

    // 断网且这份资源没缓存过：打开页面时退回首屏，别给用户一个错误页
    if (req.mode === 'navigate') {
      const shell = await cache.match('./index.html');
      if (shell) return shell;
    }
    return new Response('离线，且这份内容还没缓存过。', {
      status: 504,
      headers: { 'Content-Type': 'text/plain; charset=utf-8' }
    });
  })());
});
