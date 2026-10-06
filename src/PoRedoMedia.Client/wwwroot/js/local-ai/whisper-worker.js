/**
 * Speech worker — Whisper (tiny) through transformers.js.
 *
 * Receives { samples } (mono, 16 kHz, Float32Array) and emits
 * { type: 'status', percent } while the model downloads, then
 * { type: 'complete', segments: [{ startSeconds, endSeconds, text }] } or { type: 'error', reason }.
 */

// Pinned for the same reason as the vision worker: an unpinned import would let a release change
// behaviour without a deploy.
import { pipeline, env } from 'https://cdn.jsdelivr.net/npm/@huggingface/transformers@3.7.6';

env.allowLocalModels = false;

self.onmessage = async (event) => {
    try {
        const transcriber = await pipeline('automatic-speech-recognition', 'Xenova/whisper-tiny', {
            dtype: 'q8',
            progress_callback: (p) => {
                if (p && p.status === 'progress' && typeof p.progress === 'number') {
                    self.postMessage({ type: 'status', percent: Math.round(p.progress) });
                }
            },
        });

        self.postMessage({ type: 'status', percent: -1 });
        const result = await transcriber(event.data.samples, { return_timestamps: true, chunk_length_s: 30, stride_length_s: 5 });
        const segments = (result.chunks || [])
            .filter((c) => c.text && c.text.trim() && c.timestamp && c.timestamp[0] !== null)
            .map((c) => ({
                startSeconds: c.timestamp[0],
                // The last chunk of a clip can come back without an end.
                endSeconds: c.timestamp[1] ?? c.timestamp[0] + 3,
                text: c.text.trim(),
            }));
        self.postMessage({ type: 'complete', segments });
    } catch (err) {
        self.postMessage({ type: 'error', reason: err?.message ?? String(err) });
    }
};
