/**
 * Text worker — MLC WebLLM.
 *
 * Receives {modelReference, device, prompt} and emits status/complete/error, the same protocol the
 * transformers worker uses. WebLLM has no dtype parameter: the quantization is baked into the model
 * id, which C# has already resolved (LocalModelRegistry.ResolveModelReference) before dispatch.
 * One attempt only — the fallback chain belongs to C#.
 */

import * as webllm from 'https://esm.run/@mlc-ai/web-llm@0.2.85';

let engine = null;
let cachedModel = null;

/**
 * Tears the engine down and clears the cache marker.
 *
 * Reassigning `engine` frees nothing: the previous MLCEngine keeps its WebGPU device and multi-GB
 * weight buffers alive, so switching models stacks VRAM until an allocation fails with "device was
 * lost while loading". Failures here are swallowed — a device that is already lost throws on
 * unload, and the references still have to be cleared.
 */
async function release(why) {
    if (!engine) return;
    try {
        console.log(`[LocalAI/webllm] Releasing '${cachedModel}' — ${why}`);
        await engine.unload();
    } catch (err) {
        console.warn('[LocalAI/webllm] Unload failed (device may already be lost):', err?.message ?? err);
    } finally {
        engine = null;
        cachedModel = null;
    }
}

self.onmessage = async (event) => {
    const { modelReference, prompt, systemPrompt } = event.data || {};

    try {
        if (!engine || cachedModel !== modelReference) {
            await release('switching model');

            post({ type: 'status', stage: 'Downloading', detail: `Fetching ${modelReference}`, loadPercent: 0 });

            engine = await webllm.CreateMLCEngine(modelReference, {
                initProgressCallback: (progress) => {
                    post({
                        type: 'status',
                        stage: 'Downloading',
                        detail: progress?.text ?? 'Loading model',
                        loadPercent: Math.round((progress?.progress ?? 0) * 100),
                    });
                },
            });
            cachedModel = modelReference;
        }

        post({ type: 'status', stage: 'Running', detail: 'Generating' });

        const completion = await engine.chat.completions.create({
            messages: [
                ...(systemPrompt ? [{ role: 'system', content: systemPrompt }] : []),
                { role: 'user', content: prompt },
            ],
            stream: false,
            max_tokens: 512,
        });

        post({ type: 'complete', text: completion?.choices?.[0]?.message?.content ?? '' });
    } catch (err) {
        const reason = err?.message ?? String(err);
        console.error('[LocalAI/webllm] Failed:', reason, err);
        post({ type: 'error', reason });
        // A failed load leaves `engine` pointing at the PREVIOUS model (the assignment never ran),
        // and a device lost mid-generation leaves a dead engine whose marker still matches.
        await release('run failed');
    }
};

function post(message) {
    self.postMessage(message);
}
