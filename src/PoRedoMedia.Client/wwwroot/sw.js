// Service worker. Three jobs:
//  1. Receive a photo or video another app shares to this one, and hand it to the Create page.
//  2. Keep the app's own files so it still opens without a connection.
//  3. Keep gallery thumbnails already seen, for the same reason.
// The network is always tried first, so an online user never sees an old copy of anything.
const SHELL = 'po-shell-v1';
const THUMBS = 'po-thumbs-v1';
const SHARED = 'po-shared';

self.addEventListener('install', () => self.skipWaiting());
self.addEventListener('activate', (event) => event.waitUntil(self.clients.claim()));

self.addEventListener('fetch', (event) => {
    const request = event.request;
    const url = new URL(request.url);
    if (url.origin !== self.location.origin) return;

    if (request.method === 'POST' && url.pathname === '/share-target') {
        event.respondWith(receiveShare(request));
        return;
    }

    if (request.method !== 'GET') return;

    if (/^\/api\/media\/[^/]+\/thumb$/.test(url.pathname)) {
        event.respondWith(networkFirst(request, THUMBS));
        return;
    }

    // Everything else under /api, the hubs and the sign-in routes is live data: never kept.
    if (url.pathname.startsWith('/api/') || url.pathname.startsWith('/hubs/') || url.pathname.startsWith('/v/')
        || ['/dev-login', '/challenge-microsoft', '/signin-oidc', '/logout'].includes(url.pathname)) {
        return;
    }

    // A page navigation with no connection opens the app shell, which then shows what it can.
    event.respondWith(networkFirst(request, SHELL, request.mode === 'navigate' ? '/' : null));
});

async function networkFirst(request, cacheName, fallbackUrl) {
    const cache = await caches.open(cacheName);
    try {
        const response = await fetch(request);
        // Opaque responses are thumbnails redirected to storage; they are kept as they are.
        if (response.ok || response.type === 'opaque') cache.put(request, response.clone());
        return response;
    } catch (error) {
        const kept = await cache.match(request) || (fallbackUrl ? await cache.match(fallbackUrl) : null);
        if (kept) return kept;
        throw error;
    }
}

async function receiveShare(request) {
    try {
        const form = await request.formData();
        const file = form.get('media');
        if (file && file.size > 0) {
            const cache = await caches.open(SHARED);
            await cache.put('/shared-file', new Response(file, {
                headers: { 'Content-Type': file.type || 'application/octet-stream', 'X-File-Name': encodeURIComponent(file.name || 'shared') },
            }));
        }
    } catch { /* nothing usable was shared; the page opens empty */ }
    return Response.redirect('/?shared=1', 303);
}
