/**
 * Depth worker — transformers.js / ONNX Runtime Web, Depth Anything V2 (small, ~25 MB quantised).
 *
 * Receives {blob, webgpu} and emits status/complete/error. The result is one byte per pixel,
 * brighter for nearer, at the picture's own size. Used for the moving preview shown while a photo
 * is being turned into a video.
 */

// Pinned for the same reason as the vision worker: an unpinned import would let a release change
// behaviour without a deploy.
import { pipeline, env, RawImage } from 'https://cdn.jsdelivr.net/npm/@huggingface/transformers@3.7.6';

env.allowLocalModels = false;

let estimator = null;

self.onmessage = async ({ data }) => {
    try {
        if (!estimator) {
            const device = data.webgpu ? 'webgpu' : 'wasm';
            estimator = await pipeline('depth-estimation', 'onnx-community/depth-anything-v2-small', {
                device,
                dtype: device === 'webgpu' ? 'fp16' : 'q8',
                progress_callback: (p) => {
                    if (p && p.status === 'progress' && typeof p.progress === 'number') {
                        self.postMessage({ type: 'status', percent: Math.round(p.progress) });
                    }
                },
            });
        }
        self.postMessage({ type: 'status', percent: 100 });
        const image = await RawImage.fromBlob(data.blob);
        const { depth } = await estimator(image);
        const bytes = depth.data instanceof Uint8Array ? depth.data : Uint8Array.from(depth.data);
        self.postMessage({ type: 'complete', width: depth.width, height: depth.height, channels: depth.channels, data: bytes }, [bytes.buffer]);
    } catch (err) {
        estimator = null;
        self.postMessage({ type: 'error', reason: String(err?.message ?? err) });
    }
};
