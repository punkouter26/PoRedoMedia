// A moving 2.5D preview of a photo, shown while the photo is being turned into a video, which
// can take minutes. The photo starts drifting at once; meanwhile a depth model runs in this
// browser (Depth Anything V2 in a worker), and when its depth map arrives the picture opens up
// into layers: the camera dollies slowly, follows the pointer or the phone's tilt, near things
// move more than far ones, and what is out of focus blurs.
//
// The shader is parallax occlusion mapping: for each screen pixel it walks from near to far
// through 32 depth layers until it finds the first surface the shifted view ray meets, which
// gives proper occlusion instead of the rubber-sheet smear of a plain depth offset. Depth of field
// is a 12-tap golden-angle disc blur sized by how far that surface is from the focus.
window.poDepth = (() => {
    const vertex = `#version 300 es
        in vec2 p; out vec2 uv;
        void main() { uv = p * 0.5 + 0.5; gl_Position = vec4(p, 0.0, 1.0); }`;

    const fragment = `#version 300 es
        precision highp float;
        in vec2 uv;
        uniform sampler2D picture; uniform sampler2D depth;
        uniform vec2 screen; uniform vec2 pictureSize; uniform vec2 camera;
        uniform float zoom; uniform float amount; uniform float focus; uniform float time;
        out vec4 colour;

        float depthAt(vec2 q) { return mix(0.5, texture(depth, q).r, amount); }

        void main() {
            // Cover the stage with the picture, keeping its shape.
            vec2 scale = screen / pictureSize;
            float s = max(scale.x, scale.y);
            vec2 q = (uv - 0.5) * screen / (pictureSize * s);
            q = q / zoom + 0.5 + camera * 0.012;
            q.y = 1.0 - q.y;

            // March from near (1) to far (0) and stop at the first surface the shifted ray meets.
            vec2 shift = vec2(camera.x, -camera.y) * 0.05;
            const int LAYERS = 32;
            float z = 1.0, previousZ = 1.0, previousGap = 0.0;
            vec2 hit = q;
            for (int i = 0; i <= LAYERS; i++) {
                z = 1.0 - float(i) / float(LAYERS);
                vec2 at = q - shift * (z - focus);
                float gap = depthAt(at) - z;
                if (gap >= 0.0) {
                    // Between this layer and the last one: interpolate to where the surface lies.
                    float t = previousGap / (previousGap - gap + 1e-5);
                    float exact = mix(previousZ, z, clamp(t, 0.0, 1.0));
                    hit = q - shift * (exact - focus);
                    break;
                }
                previousZ = z;
                previousGap = gap;
            }

            // Depth of field: a disc blur whose size grows with distance from the focus.
            float blur = abs(depthAt(hit) - focus) * amount * 0.006;
            vec3 sum = texture(picture, hit).rgb;
            float weight = 1.0;
            for (int i = 1; i < 12; i++) {
                float r = sqrt(float(i) / 12.0) * blur;
                float angle = float(i) * 2.39996;
                vec2 o = vec2(cos(angle), sin(angle)) * r;
                sum += texture(picture, hit + o).rgb;
                weight += 1.0;
            }
            vec3 c = sum / weight;

            // A little film: a vignette, a warm light leak that drifts, and grain.
            vec2 centred = uv - 0.5;
            c *= 1.0 - dot(centred, centred) * 0.9;
            c += vec3(1.0, 0.55, 0.25) * 0.07 * smoothstep(0.6, 0.0, length(uv - vec2(0.1 + 0.08 * sin(time * 0.3), 0.9)));
            c += (fract(sin(dot(uv * screen + time, vec2(12.9898, 78.233))) * 43758.5453) - 0.5) * 0.035;
            colour = vec4(c, 1.0);
        }`;

    async function begin(host, imageUrl) {
        if (!host || host._poDepth || !window.poFx.enabled()) return;
        const state = host._poDepth = { amount: 0, target: 0, pointer: [0, 0], tilt: [0, 0], worker: null };
        const canvas = document.createElement('canvas');
        const status = document.createElement('span');
        status.className = 'run-depth-status';
        host.append(canvas, status);
        const gl = canvas.getContext('webgl2');
        if (!gl) { host.hidden = true; return; }

        let program;
        try {
            program = window.poFx.program(gl, vertex, fragment);
        } catch (err) {
            console.warn('[poDepth]', err.message);
            host.hidden = true;
            return;
        }
        gl.useProgram(program);
        gl.bindBuffer(gl.ARRAY_BUFFER, gl.createBuffer());
        gl.bufferData(gl.ARRAY_BUFFER, new Float32Array([-1, -1, 3, -1, -1, 3]), gl.STATIC_DRAW);
        const at = gl.getAttribLocation(program, 'p');
        gl.enableVertexAttribArray(at);
        gl.vertexAttribPointer(at, 2, gl.FLOAT, false, 0, 0);
        const u = (name) => gl.getUniformLocation(program, name);

        const texture = (unit, configure) => {
            const t = gl.createTexture();
            gl.activeTexture(gl.TEXTURE0 + unit);
            gl.bindTexture(gl.TEXTURE_2D, t);
            gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MIN_FILTER, gl.LINEAR);
            gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MAG_FILTER, gl.LINEAR);
            gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_S, gl.CLAMP_TO_EDGE);
            gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_T, gl.CLAMP_TO_EDGE);
            configure();
            return t;
        };

        let picture;
        try {
            picture = await window.poFx.bitmap(imageUrl);
        } catch {
            host.hidden = true;
            return;
        }
        texture(0, () => gl.texImage2D(gl.TEXTURE_2D, 0, gl.RGBA, gl.RGBA, gl.UNSIGNED_BYTE, picture));
        texture(1, () => gl.texImage2D(gl.TEXTURE_2D, 0, gl.R8, 1, 1, 0, gl.RED, gl.UNSIGNED_BYTE, new Uint8Array([128])));
        gl.uniform1i(u('picture'), 0);
        gl.uniform1i(u('depth'), 1);
        gl.uniform2f(u('pictureSize'), picture.width, picture.height);
        gl.uniform1f(u('focus'), 0.6);

        // The pointer over the stage, or the phone's tilt, steers the camera.
        host.addEventListener('pointermove', (e) => {
            const r = host.getBoundingClientRect();
            state.pointer = [(e.clientX - r.left) / r.width * 2 - 1, (e.clientY - r.top) / r.height * 2 - 1];
        });
        host.addEventListener('pointerleave', () => { state.pointer = [0, 0]; });
        const onTilt = (e) => {
            if (e.gamma === null) return;
            state.tilt = [Math.max(-1, Math.min(1, e.gamma / 25)), Math.max(-1, Math.min(1, (e.beta - 45) / 25))];
        };
        window.addEventListener('deviceorientation', onTilt);

        const began = performance.now();
        const camera = [0, 0];
        const frame = (now) => {
            if (!host.isConnected) {
                window.removeEventListener('deviceorientation', onTilt);
                state.worker?.terminate();
                gl.getExtension('WEBGL_lose_context')?.loseContext();
                return;
            }
            const t = (now - began) / 1000;
            state.amount += (state.target - state.amount) * 0.03;
            // A slow figure-of-eight dolly, plus wherever the viewer is pointing.
            const goal = [Math.sin(t * 0.37) * 0.55 + state.pointer[0] * 0.7 + state.tilt[0] * 0.7,
                Math.sin(t * 0.23) * 0.35 + state.pointer[1] * 0.5 + state.tilt[1] * 0.5];
            camera[0] += (goal[0] - camera[0]) * 0.06;
            camera[1] += (goal[1] - camera[1]) * 0.06;
            window.poFx.fit(canvas, 1.5);
            gl.viewport(0, 0, canvas.width, canvas.height);
            gl.uniform2f(u('screen'), canvas.width, canvas.height);
            gl.uniform2f(u('camera'), camera[0], camera[1]);
            gl.uniform1f(u('zoom'), 1.1 + 0.05 * Math.sin(t * 0.2));
            gl.uniform1f(u('amount'), state.amount);
            gl.uniform1f(u('time'), t);
            gl.drawArrays(gl.TRIANGLES, 0, 3);
            requestAnimationFrame(frame);
        };
        requestAnimationFrame(frame);

        // The depth model works on a copy at most 768 px across: it sees 518 px anyway.
        const scale = Math.min(1, 768 / Math.max(picture.width, picture.height));
        const small = document.createElement('canvas');
        small.width = Math.round(picture.width * scale);
        small.height = Math.round(picture.height * scale);
        small.getContext('2d').drawImage(picture, 0, 0, small.width, small.height);
        const blob = await new Promise((resolve) => small.toBlob(resolve, 'image/jpeg', 0.9));

        const estimate = (webgpu) => new Promise((resolve, reject) => {
            state.worker?.terminate();
            state.worker = new Worker('js/local-ai/depth-worker.js', { type: 'module' });
            state.worker.onmessage = (e) => {
                if (e.data.type === 'status') {
                    status.textContent = e.data.percent < 100 ? `Reading the depth of the picture on your device… ${e.data.percent}%` : 'Reading the depth of the picture on your device…';
                } else if (e.data.type === 'complete') {
                    resolve(e.data);
                } else {
                    reject(new Error(e.data.reason));
                }
            };
            state.worker.onerror = (e) => reject(new Error(e.message));
            state.worker.postMessage({ blob, webgpu });
        });

        if (navigator.connection?.saveData) return;
        status.textContent = 'Reading the depth of the picture on your device…';
        let result;
        try {
            result = await estimate(Boolean(navigator.gpu)).catch(() => estimate(false));
        } catch (err) {
            console.warn('[poDepth] depth model:', err?.message ?? err);
            status.textContent = '';
            state.worker?.terminate();
            return;
        }
        state.worker?.terminate();
        state.worker = null;
        if (!host.isConnected) return;

        // One channel, softened a little so edges do not tear when the view moves.
        const { width, height, channels, data } = result;
        const single = new Uint8Array(width * height);
        for (let i = 0; i < single.length; i++) single[i] = data[i * (channels || 1)];
        const soft = blurred(single, width, height, 3);
        // Focus on whatever is in the middle of the picture.
        let sum = 0, n = 0;
        for (let y = Math.floor(height * 0.35); y < height * 0.65; y++) {
            for (let x = Math.floor(width * 0.35); x < width * 0.65; x++) { sum += soft[y * width + x]; n++; }
        }
        gl.activeTexture(gl.TEXTURE1);
        gl.pixelStorei(gl.UNPACK_ALIGNMENT, 1);
        gl.texImage2D(gl.TEXTURE_2D, 0, gl.R8, width, height, 0, gl.RED, gl.UNSIGNED_BYTE, soft);
        gl.uniform1f(u('focus'), n ? sum / n / 255 : 0.6);
        state.target = 1;
        status.textContent = '';
    }

    // A box blur, once across and once down.
    function blurred(source, w, h, radius) {
        const across = new Uint8Array(source.length), out = new Uint8Array(source.length);
        for (let y = 0; y < h; y++) {
            for (let x = 0; x < w; x++) {
                let sum = 0, n = 0;
                for (let k = -radius; k <= radius; k++) {
                    const xx = x + k;
                    if (xx >= 0 && xx < w) { sum += source[y * w + xx]; n++; }
                }
                across[y * w + x] = sum / n;
            }
        }
        for (let y = 0; y < h; y++) {
            for (let x = 0; x < w; x++) {
                let sum = 0, n = 0;
                for (let k = -radius; k <= radius; k++) {
                    const yy = y + k;
                    if (yy >= 0 && yy < h) { sum += across[yy * w + x]; n++; }
                }
                out[y * w + x] = sum / n;
            }
        }
        return out;
    }

    return {
        /** Starts the preview inside `host`, without waiting for it. It stops by itself when the host leaves the page. */
        start(host, imageUrl) {
            begin(host, imageUrl).catch((err) => console.warn('[poDepth]', err?.message ?? err));
        },
    };
})();
