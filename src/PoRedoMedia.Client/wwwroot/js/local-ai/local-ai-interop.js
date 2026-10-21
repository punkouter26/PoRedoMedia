/**
 * Bridge between the Blazor LocalAi services and the transformers.js inference worker.
 *
 * The worker speaks a small postMessage protocol — {type:'status'|'complete'|'error'}. All
 * fallback POLICY lives in C# (DtypeChain); this layer only executes one attempt at whatever
 * variant it is handed.
 */

const workers = {};
const WORKER_SCRIPT = '/js/local-ai/transformers-worker.js';

/**
 * Probes the GPU so C# can prune the dtype chain before any weights are fetched.
 * Never throws: a browser with no WebGPU is a supported configuration (wasm), not an error.
 */
window.poLocalAiProbeDevice = async function () {
    const result = {
        hasWebGpu: false,
        hasShaderF16: false,
        maxBufferBytes: null,
        adapterDescription: null,
    };

    try {
        if (typeof navigator === 'undefined' || !navigator.gpu) return result;

        const adapter = await navigator.gpu.requestAdapter();
        if (!adapter) return result;

        result.hasWebGpu = true;
        result.hasShaderF16 = adapter.features ? adapter.features.has('shader-f16') : false;

        const limits = adapter.limits || {};
        if (typeof limits.maxBufferSize === 'number') result.maxBufferBytes = limits.maxBufferSize;

        let info = {};
        try {
            info = adapter.info || (adapter.requestAdapterInfo ? await adapter.requestAdapterInfo() : {});
        } catch { /* adapter info is optional and absent on some drivers */ }
        result.adapterDescription = [info.vendor, info.architecture, info.device]
            .filter(Boolean).join(' ') || null;
    } catch (err) {
        console.warn('[LocalAI] Device probe failed:', err?.message ?? err);
    }

    return result;
};

/**
 * Runs ONE attempt at the given variant. C# owns retry/fallback, so any failure here is reported
 * straight back rather than being retried internally.
 *
 * @param dotnetRef  DotNetObjectReference exposing ReceiveStatus / ReceiveComplete / ReceiveError.
 * @param runId      Correlates callbacks with the awaiting C# task.
 */
window.poLocalAiRun = function (dotnetRef, runId, options) {
    terminate(runId);

    let worker;
    try {
        worker = new Worker(WORKER_SCRIPT, { type: 'module' });
    } catch (err) {
        dotnetRef.invokeMethodAsync('ReceiveError', runId, `Worker failed to start: ${err?.message ?? err}`);
        return;
    }
    workers[runId] = worker;

    worker.onmessage = (event) => {
        const data = event.data || {};
        switch (data.type) {
            case 'status':
                dotnetRef.invokeMethodAsync('ReceiveStatus', runId, data.stage ?? 'Loading',
                    data.detail ?? null, typeof data.loadPercent === 'number' ? data.loadPercent : null);
                break;
            case 'complete':
                dotnetRef.invokeMethodAsync('ReceiveComplete', runId, data.text ?? '');
                terminate(runId);
                break;
            case 'error':
                dotnetRef.invokeMethodAsync('ReceiveError', runId, data.reason ?? 'Unknown worker error');
                terminate(runId);
                break;
            default:
                console.warn('[LocalAI] Unrecognised worker message:', data);
        }
    };

    // An uncaught worker error never produces a 'complete', so without this the C# task would
    // await forever rather than failing and advancing the chain.
    worker.onerror = (err) => {
        dotnetRef.invokeMethodAsync('ReceiveError', runId, err?.message ?? 'Worker crashed');
        terminate(runId);
    };

    worker.postMessage(options);
};

/** Cancels a run — also how C# releases GPU memory before trying the next variant. */
window.poLocalAiCancel = function (runId) {
    terminate(runId);
};

function terminate(runId) {
    const existing = workers[runId];
    if (!existing) return;
    try {
        existing.terminate();
    } catch { /* already gone */ }
    delete workers[runId];
}
