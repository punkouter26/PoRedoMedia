/**
 * Cutout worker — BiRefNet lite (MIT licence) through transformers.js / ONNX Runtime Web.
 *
 * Receives { blob, webgpu } and emits { type: 'status', percent } while the model downloads, then
 * { type: 'complete', mask, width, height } — one alpha byte per pixel at the picture's own size —
 * or { type: 'error', reason }. The page applies the mask, so the colours never leave full quality.
 */

// Pinned for the same reason as the vision worker: an unpinned import would let a release change
// behaviour without a deploy.
import { AutoModel, AutoProcessor, RawImage, env } from 'https://cdn.jsdelivr.net/npm/@huggingface/transformers@3.7.6';

env.allowLocalModels = false;

const MODEL = 'onnx-community/BiRefNet_lite-ONNX';
let loaded = null;

async function load(webgpu) {
    const progress_callback = (p) => {
        if (p && p.status === 'progress' && typeof p.progress === 'number') {
            self.postMessage({ type: 'status', percent: Math.round(p.progress) });
        }
    };
    // fp16 halves the download (115 MB) where the GPU runs it; the CPU path needs fp32.
    const device = webgpu ? 'webgpu' : 'wasm';
    const model = await AutoModel.from_pretrained(MODEL, { device, dtype: webgpu ? 'fp16' : 'fp32', progress_callback });
    const processor = await AutoProcessor.from_pretrained(MODEL);
    return { model, processor };
}

self.onmessage = async ({ data }) => {
    try {
        loaded ??= await load(data.webgpu);
        self.postMessage({ type: 'status', percent: -1 });
        const image = await RawImage.fromBlob(data.blob);
        const { pixel_values } = await loaded.processor(image);
        const { output_image } = await loaded.model({ input_image: pixel_values });
        const mask = await RawImage.fromTensor(output_image[0].sigmoid().mul(255).to('uint8')).resize(image.width, image.height);
        const bytes = mask.data instanceof Uint8Array ? mask.data : Uint8Array.from(mask.data);
        self.postMessage({ type: 'complete', mask: bytes, width: mask.width, height: mask.height, channels: mask.channels }, [bytes.buffer]);
    } catch (err) {
        loaded = null;
        self.postMessage({ type: 'error', reason: String(err?.message ?? err) });
    }
};
