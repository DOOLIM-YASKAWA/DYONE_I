// Windows 앱이 모든 페이지에 주입하는 브리지.
// 웹(React)은 window.dyoneDesktop 유무로 설치형 앱인지 판단하고, invoke()로 .NET 기능을 호출한다(web/src/platform/bridge.ts).
(() => {
  if (!window.chrome || !window.chrome.webview) return;
  // __DYONE_ORIGIN__ 은 앱이 설정(AppUrl)의 출처로 바꿔 넣는다. 다른 출처의 페이지에는 브리지를 열지 않는다.
  if (location.origin !== '__DYONE_ORIGIN__') return;

  const webview = window.chrome.webview;
  const pending = new Map();
  let seq = 0;

  webview.addEventListener('message', (event) => {
    const msg = event.data;
    const waiter = msg && pending.get(msg.id);
    if (!waiter) return;
    pending.delete(msg.id);
    if (msg.ok) waiter.resolve(msg.result);
    else waiter.reject(new Error(msg.error || '실패'));
  });

  const invoke = (cmd, args = {}) => new Promise((resolve, reject) => {
    const id = `r${++seq}`;
    pending.set(id, { resolve, reject });
    webview.postMessage({ id, cmd, args });
  });

  Object.defineProperty(window, 'dyoneDesktop', { value: Object.freeze({ isDesktop: true, invoke }) });
})();
