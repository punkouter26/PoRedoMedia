/**
 * Vision worker — transformers.js / ONNX Runtime Web.
 *
 * Receives {repoId, dtype, device, prompt, imageBase64} and emits status/complete/error.
 * It attempts exactly ONE dtype: the fallback chain is owned by C# (DtypeChain), so a failure here
 * is reported rather than retried.
 */

// Pinned to a specific version: an unpinned CDN import would let a transformers.js release change
// inference behaviour without a deploy. Vendor this file to wwwroot to drop the CDN entirely.
import {
    AutoProcessor, AutoTokenizer, AutoModelForVision2Seq, RawImage, env,
} from 'https://cdn.jsdelivr.net/npm/@huggingface/transformers@3.7.6';

// The worker fetches weights itself; disabling the local-model path stops it probing a /models/
// route that this app does not serve and logging a 404 for every shard.
env.allowLocalModels = false;

let cached = null; // { key, processor, tokenizer, model }

async function release(why) {
    if (!cached) return;
    try {
        console.log(`[LocalAI/transformers] Releasing '${cached.key}' — ${why}`);
        await cached.model?.dispose?.();
    } catch (err) {
        console.warn('[LocalAI/transformers] Dispose failed:', err?.message ?? err);
    } finally {
        cached = null;
    }
}

self.onmessage = async (event) => {
    const { repoId, dtype, device, prompt, imageBase64 } = event.data || {};
    const key = `${repoId}|${dtype}|${device}`;

    try {
        if (!cached || cached.key !== key) {
            // Free the previous model FIRST. Reassigning does not release its GPU buffers, and
            // stacking them is what produces "device was lost" on the second model load.
            await release('variant or model changed');

            post({ type: 'status', stage: 'Downloading', detail: `Fetching ${repoId} (${dtype})`, loadPercent: 0 });

            const onProgress = (p) => {
                if (p && p.status === 'progress' && typeof p.progress === 'number') {
                    post({
                        type: 'status',
                        stage: 'Downloading',
                        detail: p.file ? `Downloading ${p.file}` : 'Downloading weights',
                        loadPercent: Math.round(p.progress),
                    });
                }
            };

            const [processor, tokenizer, model] = await Promise.all([
                AutoProcessor.from_pretrained(repoId, { progress_callback: onProgress }),
                AutoTokenizer.from_pretrained(repoId, { progress_callback: onProgress }),
                AutoModelForVision2Seq.from_pretrained(repoId, {
                    dtype,
                    device: device === 'WebGpu' ? 'webgpu' : 'wasm',
                    progress_callback: onProgress,
                }),
            ]);

            cached = { key, processor, tokenizer, model };
        }

        post({ type: 'status', stage: 'Running', detail: 'Describing the image' });

        const image = await RawImage.fromBlob(base64ToBlob(imageBase64));
        const task = prompt && prompt.trim() ? prompt : '<MORE_DETAILED_CAPTION>';

        const inputs = await cached.processor(image, task);
        const generated = await cached.model.generate({ ...inputs, max_new_tokens: 256 });
        const decoded = cached.tokenizer.batch_decode(generated, { skip_special_tokens: true });

        post({ type: 'complete', text: (decoded?.[0] ?? '').trim() });
    } catch (err) {
        const reason = err?.message ?? String(err);
        console.error('[LocalAI/transformers] Failed:', reason, err);
        post({ type: 'error', reason });
        // A failed load can leave a half-initialised model behind; the next attempt (a different
        // dtype) must not reuse it.
        await release('run failed');
    }
};

function post(message) {
    self.postMessage(message);
}

function base64ToBlob(base64) {
    const binary = atob(base64);
    const bytes = new Uint8Array(binary.length);
    for (let i = 0; i < binary.length; i++) bytes[i] = binary.charCodeAt(i);
    return new Blob([bytes]);
}
