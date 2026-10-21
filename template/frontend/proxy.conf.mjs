// 本机开发时浏览器只访问前端开发服务器，下列路径转发给本机后端：前后端同源，Cookie 与 OIDC 回调都按这个地址生成。
// 不改写 Host（不开 changeOrigin）：后端看到的是浏览器访问的地址，生成的回调、签发方与 Cookie 才对得上。
// 后端不在默认端口时，启动前设置 API_PROXY_TARGET，例如 API_PROXY_TARGET=http://localhost:5300 npm start。
const target = process.env['API_PROXY_TARGET'] || 'http://localhost:5240';

export default {
  '/api/**': { target, secure: false },
  // SignalR 走 WebSocket，只有这一条需要转发协议升级
  '/hubs/**': { target, secure: false, ws: true },
  //#if (OpenIddictServer)
  '/connect/**': { target, secure: false },
  '/.well-known/**': { target, secure: false },
  //#endif
};
