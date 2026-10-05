/**
 * Bridge between the Blazor LocalAi services and the two inference workers.
 *
 * Both workers speak an identical postMessage protocol — {type:'status'|'complete'|'error'} — so
 * this file is runtime-agnostic: it picks a worker script by runtime name and otherwise treats
 * them the same. All fallback POLICY lives in C# (DtypeChain); this layer only executes one
 * attempt at whatever variant it is handed.
 */

const workers = {};

// Availability probes are deduplicated and cached: the model picker renders one row per model and
// would otherwise fire a burst of identical HEAD requests on every render.
const availabilityCache = new Map();
const availabilityInflight = new Map();
const AVAILABILITY_TTL_MS = 15000;

const WORKER_SCRIPTS = {
    WebLlm: '/js/local-ai/webllm-worker.js',
    TransformersJs: '/js/local-ai/transformers-worker.js',
};

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
 * Checks whether a model's files are reachable, trying a vendored local copy before the CDNs.
 * Mirrors the source-fallback chain proven in PoLocalCompare, and is independent of the dtype
 * chain — one is about WHERE the weights come from, the other about WHICH weights.
 */
window.poLocalAiResolveAvailability = async function (repoId, localBaseUrl, cdnTemplates) {
    const key = `${repoId}|${localBaseUrl}|${(cdnTemplates || []).join('|')}`;
    const cached = availabilityCache.get(key);
    if (cached && Date.now() - cached.at < AVAILABILITY_TTL_MS) return cached.value;
    if (availabilityInflight.has(key)) return availabilityInflight.get(key);

    const promise = (async () => {
        if (localBaseUrl) {
            const local = joinUrl(localBaseUrl, repoId);
            if (await canReach(local)) return { available: true, source: 'local', baseUrl: local };
        }

        const templates = Array.isArray(cdnTemplates) ? cdnTemplates.filter(Boolean) : [];
        for (let i = 0; i < templates.length; i++) {
            const url = templates[i].includes('{repoId}')
                ? templates[i].replaceAll('{repoId}', repoId)
                : joinUrl(templates[i], repoId);
            if (await canReach(url)) {
                return { available: true, source: i === 0 ? 'cdn' : 'cdn-backup', baseUrl: url };
            }
        }

        return { available: false, source: '', baseUrl: '' };
    })();

    availabilityInflight.set(key, promise);
    try {
        const value = await promise;
        availabilityCache.set(key, { at: Date.now(), value });
        return value;
    } finally {
        availabilityInflight.delete(key);
    }
};

function joinUrl(base, suffix) {
    const b = base.endsWith('/') ? base : `${base}/`;
    return `${b}${suffix}`;
}

async function canReach(url) {
    if (!url) return false;
    try {
        const controller = new AbortController();
        const timeout = setTimeout(() => controller.abort(), 3500);
        try {
            // HEAD on the repo root: cheap, and enough to tell "reachable" from "blocked/absent".
            const response = await fetch(url, { method: 'HEAD', signal: controller.signal, cache: 'no-store' });
            return response.ok;
        } finally {
            clearTimeout(timeout);
        }
    } catch {
        return false;
    }
}

/**
 * Runs ONE attempt at the given variant. C# owns retry/fallback, so any failure here is reported
 * straight back rather than being retried internally.
 *
 * @param dotnetRef  DotNetObjectReference exposing ReceiveStatus / ReceiveComplete / ReceiveError.
 * @param runId      Correlates callbacks with the awaiting C# task.
 */
window.poLocalAiRun = function (dotnetRef, runId, options) {
    const script = WORKER_SCRIPTS[options.runtime];
    if (!script) {
        dotnetRef.invokeMethodAsync('ReceiveError', runId, `Unknown runtime '${options.runtime}'.`);
        return;
    }

    terminate(runId);

    let worker;
    try {
        worker = new Worker(script, { type: 'module' });
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
