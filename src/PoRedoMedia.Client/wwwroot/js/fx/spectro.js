// The sound library as a wall of living spectrograms: each sound's row shows a heat-map ribbon of
// its frequencies over time (worked out in a worker, only for rows that are on screen, two at a
// time). Hovering shows where a click would play from; a click plays from there. The sound that
// is playing gets a glowing playhead, the part already heard lights up, and its ribbon pulses
// with the sound's loudness.
window.poSpectro = (() => {
    const COLUMNS = 180, ROWS = 36;
    const results = new Map();
    let observer = null, watcher = null, player = null, queue = [], working = 0, frame = 0;

    // An "inferno"-like heat ramp: black, purple, red, orange, pale yellow.
    const ramp = (() => {
        const stops = [[0, [8, 6, 26]], [0.3, [86, 17, 110]], [0.55, [187, 55, 84]], [0.78, [249, 142, 9]], [1, [252, 255, 164]]];
        const table = new Uint8ClampedArray(256 * 4);
        for (let i = 0; i < 256; i++) {
            const t = i / 255;
            const k = stops.findIndex((s) => s[0] >= t);
            const [t1, c1] = stops[Math.max(0, k - 1)], [t2, c2] = stops[Math.max(0, k)];
            const f = t2 === t1 ? 0 : (t - t1) / (t2 - t1);
            table.set([...c1.map((c, j) => c + (c2[j] - c) * f), 255], i * 4);
        }
        return table;
    })();

    async function picture(result) {
        const pixels = new ImageData(COLUMNS, ROWS);
        for (let i = 0; i < result.image.length; i++) pixels.data.set(ramp.subarray(result.image[i] * 4, result.image[i] * 4 + 4), i * 4);
        return createImageBitmap(pixels);
    }

    function draw(canvas, playedFraction = -1, hoverFraction = -1) {
        const entry = results.get(canvas.dataset.spectro);
        if (!entry?.bitmap) return;
        const density = window.poFx.fit(canvas, 2);
        const g = canvas.getContext('2d');
        const w = canvas.width, h = canvas.height;
        g.imageSmoothingEnabled = true;
        g.imageSmoothingQuality = 'high';
        g.drawImage(entry.bitmap, 0, 0, w, h);
        if (playedFraction >= 0) {
            // Not yet heard: dimmed. The playhead: a bright line with a soft glow behind it.
            const x = playedFraction * w;
            g.fillStyle = 'rgba(0,0,0,0.45)';
            g.fillRect(x, 0, w - x, h);
            const glow = g.createLinearGradient(x - 18 * density, 0, x, 0);
            glow.addColorStop(0, 'rgba(255,255,255,0)');
            glow.addColorStop(1, 'rgba(255,255,255,0.45)');
            g.fillStyle = glow;
            g.fillRect(x - 18 * density, 0, 18 * density, h);
            g.fillStyle = '#fff';
            g.fillRect(x - density, 0, density * 2, h);
        }
        if (hoverFraction >= 0) {
            g.fillStyle = 'rgba(255,255,255,0.7)';
            for (let y = 0; y < h; y += 4 * density) g.fillRect(hoverFraction * w - density / 2, y, density, 2 * density);
        }
    }

    function next() {
        while (working < 2 && queue.length) {
            const canvas = queue.shift();
            const url = canvas.dataset.spectro;
            if (results.has(url)) { draw(canvas); continue; }
            working++;
            results.set(url, {});
            window.poFx.spectrogram(url, COLUMNS, ROWS)
                .then(async (result) => {
                    results.set(url, { ...result, bitmap: await picture(result) });
                    document.querySelectorAll(`canvas[data-spectro="${url}"]`).forEach((c) => draw(c));
                })
                .catch(() => results.delete(url))
                .finally(() => { working--; next(); });
        }
    }

    function watch(canvas) {
        if (canvas._poSpectro) return;
        canvas._poSpectro = true;
        if (results.get(canvas.dataset.spectro)?.bitmap) draw(canvas);
        else observer.observe(canvas);
        canvas.addEventListener('pointermove', (e) => {
            const r = canvas.getBoundingClientRect();
            draw(canvas, playingFraction(canvas), (e.clientX - r.left) / r.width);
        });
        canvas.addEventListener('pointerleave', () => draw(canvas, playingFraction(canvas)));
        canvas.addEventListener('click', (e) => {
            const r = canvas.getBoundingClientRect();
            seek(canvas, (e.clientX - r.left) / r.width);
        });
    }

    const isPlaying = (canvas) => player && player.dataset.url === canvas.dataset.spectro && !player.paused;
    const playingFraction = (canvas) => isPlaying(canvas) && player.duration ? player.currentTime / player.duration : -1;

    // Plays the sound from `fraction` of the way in. Another sound starts through its own play
    // button, so the page knows which one is playing; the jump happens once it has loaded.
    function seek(canvas, fraction) {
        if (player.dataset.url === canvas.dataset.spectro && player.src && isFinite(player.duration)) {
            player.currentTime = fraction * player.duration;
            if (player.paused) player.play().catch(() => { });
            return;
        }
        player._poSeek = fraction;
        canvas.closest('tr')?.querySelector('button[aria-label^="Play"]')?.click();
    }

    // While something plays: its playhead moves and its ribbon pulses with its loudness.
    function animate() {
        const playing = document.querySelector(`canvas[data-spectro="${CSS.escape(player.dataset.url || '')}"]`);
        document.querySelectorAll('canvas[data-spectro].playing').forEach((c) => {
            if (c !== playing || player.paused) { c.classList.remove('playing'); c.style.removeProperty('--beat'); draw(c); }
        });
        if (playing && !player.paused) {
            playing.classList.add('playing');
            const fraction = player.duration ? player.currentTime / player.duration : 0;
            draw(playing, fraction);
            const loudness = results.get(playing.dataset.spectro)?.loudness;
            if (loudness && window.poFx.enabled()) playing.style.setProperty('--beat', (loudness[Math.min(COLUMNS - 1, Math.floor(fraction * COLUMNS))] || 0).toFixed(3));
            frame = requestAnimationFrame(animate);
        } else {
            frame = 0;
        }
    }

    return {
        /** Draws a spectrogram on every canvas[data-spectro] under `container`, now and as rows come and go. */
        attach(container, audio) {
            if (!container || !window.poFx.decor()) return;
            player = audio;
            observer ??= new IntersectionObserver((entries) => {
                for (const entry of entries) {
                    if (!entry.isIntersecting) continue;
                    observer.unobserve(entry.target);
                    queue.push(entry.target);
                }
                next();
            }, { rootMargin: '200px' });
            const scan = () => container.querySelectorAll('canvas[data-spectro]').forEach(watch);
            scan();
            watcher?.disconnect();
            watcher = new MutationObserver(scan);
            watcher.observe(container, { childList: true, subtree: true });
            if (!audio._poSpectro) {
                audio._poSpectro = true;
                audio.addEventListener('play', () => { if (!frame) frame = requestAnimationFrame(animate); });
                audio.addEventListener('pause', () => { if (!frame) frame = requestAnimationFrame(animate); });
                audio.addEventListener('loadedmetadata', () => {
                    if (audio._poSeek !== undefined && isFinite(audio.duration)) audio.currentTime = audio._poSeek * audio.duration;
                    delete audio._poSeek;
                });
            }
        },

        detach() {
            watcher?.disconnect();
            observer?.disconnect();
            observer = null;
            queue = [];
        },
    };
})();
