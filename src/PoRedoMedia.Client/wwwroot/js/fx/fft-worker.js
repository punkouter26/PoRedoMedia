// Frequency analysis for the effects, off the page's thread: the loudness of a track's bass,
// mids and highs over time (the roast stage dances to them) and a small spectrogram of each
// sound in the library. Both are a short-time Fourier transform with a Hann window.

// In-place radix-2 FFT. `re` and `im` have a power-of-two length.
function fft(re, im) {
    const n = re.length;
    for (let i = 1, j = 0; i < n; i++) {
        let bit = n >> 1;
        for (; j & bit; bit >>= 1) j ^= bit;
        j ^= bit;
        if (i < j) {
            [re[i], re[j]] = [re[j], re[i]];
            [im[i], im[j]] = [im[j], im[i]];
        }
    }
    for (let size = 2; size <= n; size <<= 1) {
        const step = -2 * Math.PI / size;
        for (let start = 0; start < n; start += size) {
            for (let k = 0; k < size / 2; k++) {
                const cos = Math.cos(step * k), sin = Math.sin(step * k);
                const a = start + k, b = a + size / 2;
                const tre = re[b] * cos - im[b] * sin, tim = re[b] * sin + im[b] * cos;
                re[b] = re[a] - tre; im[b] = im[a] - tim;
                re[a] += tre; im[a] += tim;
            }
        }
    }
}

// Magnitudes of the window of `size` samples centred on `centre`.
function spectrum(samples, centre, size, window, re, im) {
    const start = Math.round(centre - size / 2);
    for (let i = 0; i < size; i++) {
        const at = start + i;
        re[i] = at >= 0 && at < samples.length ? samples[at] * window[i] : 0;
        im[i] = 0;
    }
    fft(re, im);
    const magnitudes = new Float32Array(size / 2);
    for (let k = 0; k < size / 2; k++) magnitudes[k] = Math.hypot(re[k], im[k]);
    return magnitudes;
}

function hann(size) {
    return Float32Array.from({ length: size }, (_, i) => 0.5 - 0.5 * Math.cos(2 * Math.PI * i / (size - 1)));
}

// Scales a series so its loud moments (the 98th percentile) are 1.
function normalise(values) {
    const sorted = Float32Array.from(values).sort();
    const top = sorted[Math.floor(sorted.length * 0.98)] || 1e-6;
    for (let i = 0; i < values.length; i++) values[i] = Math.min(1.4, values[i] / top);
    return values;
}

function bands({ samples, sampleRate, rate }) {
    const size = 1024, window = hann(size);
    const re = new Float32Array(size), im = new Float32Array(size);
    const frames = Math.ceil(samples.length / sampleRate * rate);
    const bin = (hz) => Math.max(1, Math.min(size / 2 - 1, Math.round(hz * size / sampleRate)));
    const ranges = [[bin(30), bin(160)], [bin(160), bin(2400)], [bin(2400), bin(11000)]];
    const out = ranges.map(() => new Float32Array(frames));
    for (let f = 0; f < frames; f++) {
        const magnitudes = spectrum(samples, f * sampleRate / rate, size, window, re, im);
        ranges.forEach(([from, to], b) => {
            let sum = 0;
            for (let k = from; k < to; k++) sum += magnitudes[k] * magnitudes[k];
            out[b][f] = Math.sqrt(sum / (to - from));
        });
    }
    const [bass, mid, high] = out.map(normalise);
    // A kick is bass rising above its recent average.
    const kick = new Float32Array(frames);
    for (let f = 8; f < frames; f++) {
        let average = 0;
        for (let i = f - 8; i < f; i++) average += bass[i];
        kick[f] = Math.max(0, bass[f] - average / 8 * 1.15);
    }
    normalise(kick);
    return { bass, mid, high, kick };
}

function spectrogram({ samples, sampleRate, columns, rows }) {
    const size = 1024, window = hann(size);
    const re = new Float32Array(size), im = new Float32Array(size);
    const levels = new Float32Array(columns * rows);
    const loudness = new Float32Array(columns);
    // Rows are spaced by pitch, not by hertz, so the low notes get as much room as the hiss.
    const low = 50, high = Math.min(12000, sampleRate / 2 - 1);
    const edge = (r) => Math.round(low * Math.pow(high / low, r / rows) * size / sampleRate);
    let peak = -Infinity;
    for (let c = 0; c < columns; c++) {
        const magnitudes = spectrum(samples, (c + 0.5) / columns * samples.length, size, window, re, im);
        let energy = 0;
        for (let k = 0; k < magnitudes.length; k++) energy += magnitudes[k] * magnitudes[k];
        loudness[c] = Math.sqrt(energy / magnitudes.length);
        for (let r = 0; r < rows; r++) {
            const from = Math.max(1, edge(r)), to = Math.max(from + 1, edge(r + 1));
            let strongest = 0;
            for (let k = from; k < to && k < magnitudes.length; k++) strongest = Math.max(strongest, magnitudes[k]);
            const db = 20 * Math.log10(strongest + 1e-9);
            levels[(rows - 1 - r) * columns + c] = db;
            peak = Math.max(peak, db);
        }
    }
    const image = new Uint8Array(columns * rows);
    // Near silence stays dark, rather than being stretched up to full heat.
    if (peak < -30) return { image, loudness: new Float32Array(columns) };
    for (let i = 0; i < levels.length; i++) image[i] = Math.max(0, Math.min(255, Math.round((levels[i] - (peak - 72)) / 72 * 255)));
    return { image, loudness: normalise(loudness) };
}

self.onmessage = ({ data }) => {
    try {
        const result = data.kind === 'bands' ? bands(data) : spectrogram(data);
        const transfer = Object.values(result).map((v) => v.buffer);
        self.postMessage({ id: data.id, ...result }, transfer);
    } catch (err) {
        self.postMessage({ id: data.id, error: String(err?.message ?? err) });
    }
};
