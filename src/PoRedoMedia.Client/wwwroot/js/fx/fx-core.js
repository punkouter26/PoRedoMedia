// The effects layer every visual flourish goes through: whether effects run at all, a WebGPU
// device when there is one, page-wide overlay canvases for effects that leave their element, and
// the small helpers the effects share (shader programs, colours, palettes, audio analysis).
//
// Effects are off until switched on in Settings ('po.fx' = 'on' in this browser's storage, which
// is also how screenshots of them are taken), and always off for a request for reduced motion.
window.poFx = (() => {
    const still = matchMedia('(prefers-reduced-motion: reduce)');
    const setting = () => window.poMedia.read('po.fx');
    let gpu = null;
    let worker = null, nextJob = 0;
    const jobs = new Map();
    const analysed = new Map();

    /** Moving effects: particles, shaders that animate. */
    function enabled() {
        return setting() === 'on' && !still.matches;
    }

    // Classes on <html> let stylesheets show an effect's element only when the effect will run.
    function refresh() {
        const root = document.documentElement;
        root.classList.toggle('po-fx', enabled());
        window.poBackdrop?.refresh?.();
    }

    refresh();
    still.addEventListener('change', refresh);

    /** A WebGPU device, or null where there is none. Asked for once and shared. */
    function device() {
        if (!navigator.gpu) return Promise.resolve(null);
        gpu ??= navigator.gpu.requestAdapter({ powerPreference: 'high-performance' })
            .then((adapter) => adapter ? adapter.requestDevice() : null)
            .then((found) => {
                found?.lost.then(() => { gpu = null; });
                return found;
            })
            .catch(() => null);
        return gpu;
    }

    /** Sizes a canvas's drawing buffer to its box at the screen's density. Returns that density. */
    function fit(canvas, maxDensity = 2) {
        const density = Math.min(maxDensity, window.devicePixelRatio || 1);
        const w = Math.max(1, Math.round(canvas.clientWidth * density));
        const h = Math.max(1, Math.round(canvas.clientHeight * density));
        if (canvas.width !== w || canvas.height !== h) { canvas.width = w; canvas.height = h; }
        return density;
    }

    /** A canvas over the whole page that never takes a click, one per effect, made when first asked for. */
    function layer(name, z = 1100) {
        let canvas = document.querySelector(`canvas[data-fx-layer="${name}"]`);
        if (!canvas) {
            canvas = document.createElement('canvas');
            canvas.dataset.fxLayer = name;
            canvas.className = 'po-fx-layer';
            canvas.style.zIndex = String(z);
            canvas.setAttribute('aria-hidden', 'true');
            document.body.appendChild(canvas);
        }
        return canvas;
    }

    /** Compiles and links a WebGL2 program, throwing the driver's message when it fails. */
    function program(gl, vertex, fragment, varyings) {
        const built = gl.createProgram();
        for (const [type, source] of [[gl.VERTEX_SHADER, vertex], [gl.FRAGMENT_SHADER, fragment]]) {
            const shader = gl.createShader(type);
            gl.shaderSource(shader, source);
            gl.compileShader(shader);
            if (!gl.getShaderParameter(shader, gl.COMPILE_STATUS)) throw new Error(gl.getShaderInfoLog(shader) || 'shader');
            gl.attachShader(built, shader);
        }
        if (varyings) gl.transformFeedbackVaryings(built, varyings, gl.INTERLEAVED_ATTRIBS);
        gl.linkProgram(built);
        if (!gl.getProgramParameter(built, gl.LINK_STATUS)) throw new Error(gl.getProgramInfoLog(built) || 'link');
        return built;
    }

    /** A CSS colour (a custom property or any colour) as [r, g, b] from 0 to 1. */
    function colour(value, fallback = '#4340d2') {
        const probe = document.createElement('span');
        const resolved = value.startsWith('--') ? getComputedStyle(document.documentElement).getPropertyValue(value).trim() : value;
        probe.style.color = resolved || fallback;
        document.body.appendChild(probe);
        const parts = (getComputedStyle(probe).color.match(/[\d.]+/g) || ['67', '64', '210']).slice(0, 3).map((v) => parseFloat(v) / 255);
        probe.remove();
        return parts;
    }

    const dark = () => document.documentElement.dataset.theme === 'dark';

    /** A picture fetched as a bitmap. Through fetch, so a picture in storage does not taint a canvas. */
    async function bitmap(url) {
        const response = await fetch(url);
        if (!response.ok) throw new Error(`HTTP ${response.status}`);
        return createImageBitmap(await response.blob());
    }

    /** A picture's pixels at w × h, cropped to fill like object-fit: cover. */
    function pixelsOf(picture, w, h) {
        const canvas = document.createElement('canvas');
        canvas.width = w;
        canvas.height = h;
        const g = canvas.getContext('2d', { willReadFrequently: true });
        const scale = Math.max(w / picture.width, h / picture.height);
        g.drawImage(picture, (w - picture.width * scale) / 2, (h - picture.height * scale) / 2, picture.width * scale, picture.height * scale);
        return g.getImageData(0, 0, w, h).data;
    }

    /**
     * The main colours of a picture, most common first, as [r, g, b] from 0 to 1. k-means on a
     * 32 × 32 copy; greys are pushed a little toward colour so a celebration never looks dull.
     */
    async function palette(url, k = 6) {
        const data = pixelsOf(await bitmap(url), 32, 32);
        const points = [];
        for (let i = 0; i < data.length; i += 4) points.push([data[i] / 255, data[i + 1] / 255, data[i + 2] / 255]);
        const luma = (c) => c[0] * 0.299 + c[1] * 0.587 + c[2] * 0.114;
        const sorted = [...points].sort((a, b) => luma(a) - luma(b));
        let centres = Array.from({ length: k }, (_, i) => [...sorted[Math.floor((i + 0.5) / k * sorted.length)]]);
        let sizes = new Array(k).fill(0);
        for (let round = 0; round < 8; round++) {
            const sums = centres.map(() => [0, 0, 0]);
            sizes = new Array(k).fill(0);
            for (const p of points) {
                let best = 0, bestDistance = Infinity;
                centres.forEach((c, i) => {
                    const d = (p[0] - c[0]) ** 2 + (p[1] - c[1]) ** 2 + (p[2] - c[2]) ** 2;
                    if (d < bestDistance) { bestDistance = d; best = i; }
                });
                sums[best][0] += p[0]; sums[best][1] += p[1]; sums[best][2] += p[2];
                sizes[best]++;
            }
            centres = centres.map((c, i) => sizes[i] ? sums[i].map((s) => s / sizes[i]) : c);
        }
        return centres
            .map((c, i) => ({ c, n: sizes[i] }))
            .filter((x) => x.n > 0)
            .sort((a, b) => b.n - a.n)
            .map(({ c }) => {
                const grey = luma(c);
                const vivid = c.map((v) => Math.min(1, Math.max(0, grey + (v - grey) * 1.35)));
                return grey < 0.18 ? vivid.map((v) => Math.min(1, v + 0.22)) : vivid;
            });
    }

    function haptic(pattern) {
        if (enabled() && navigator.vibrate) {
            try { navigator.vibrate(pattern); } catch { /* not allowed here */ }
        }
    }

    // ── Audio analysis, done in a worker ─────────────────────────────────────────────────
    function ask(message, transfer) {
        if (!worker) {
            worker = new Worker('js/fx/fft-worker.js');
            worker.onmessage = (e) => {
                const job = jobs.get(e.data.id);
                if (!job) return;
                jobs.delete(e.data.id);
                e.data.error ? job.reject(new Error(e.data.error)) : job.resolve(e.data);
            };
        }
        const id = ++nextJob;
        return new Promise((resolve, reject) => {
            jobs.set(id, { resolve, reject });
            worker.postMessage({ ...message, id }, transfer);
        });
    }

    /** A track decoded to one channel. */
    async function mono(url) {
        const bytes = await (await fetch(url)).arrayBuffer();
        const decoded = await new OfflineAudioContext(1, 1, 44100).decodeAudioData(bytes);
        const samples = new Float32Array(decoded.length);
        for (let c = 0; c < decoded.numberOfChannels; c++) {
            const channel = decoded.getChannelData(c);
            for (let i = 0; i < channel.length; i++) samples[i] += channel[i] / decoded.numberOfChannels;
        }
        return { samples, sampleRate: decoded.sampleRate, duration: decoded.duration };
    }

    /**
     * Loudness of a track's bass, mids and highs, and its kicks, `rate` values a second, each
     * scaled so its loud moments are about 1. Kept per address, so playing again costs nothing.
     */
    function bands(url, rate = 50) {
        const key = `bands|${url}|${rate}`;
        if (!analysed.has(key)) {
            analysed.set(key, mono(url).then(({ samples, sampleRate, duration }) =>
                ask({ kind: 'bands', samples, sampleRate, rate }, [samples.buffer]).then((r) => ({ ...r, rate, duration }))));
            analysed.get(key).catch(() => analysed.delete(key));
        }
        return analysed.get(key);
    }

    return {
        enabled,
        refresh,
        device,
        fit,
        layer,
        program,
        colour,
        dark,
        bitmap,
        pixelsOf,
        palette,
        haptic,
        bands,

        /** The settings switch. */
        setEnabled(on) {
            window.poMedia.write('po.fx', on ? 'on' : null);
            refresh();
        },

        /** Runs going on in the background: the backdrop brightens and a soft pad follows progress. */
        busy(level, percent) {
            window.poBackdrop?.pulse(level);
            window.poSfx?.pad(level > 0, percent);
        },
    };
})();
