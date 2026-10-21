// The pixel-flow reveal: when a new picture arrives, the picture it was made from breaks into one
// particle per pixel, and the particles fly into place as the new picture.
//
// Which source pixel becomes which result pixel: both pictures' pixels are sorted by brightness
// (then hue) and paired rank for rank, a cheap stand-in for optimal transport, so light flows to
// light and dark to dark and the picture visibly rearranges itself rather than cross-fading.
//
// With WebGPU, a compute shader runs real spring physics for every particle each frame (a spring
// pulling it along its curved path, damping, and turbulence that is strongest mid-flight), and a
// render pass draws them straight from the same storage buffer. Without WebGPU, a WebGL2 vertex
// shader works out each particle's place from the time alone, at a coarser grid.
window.poMorph = (() => {
    const running = new Set();
    const SPAN = 1.5;      // seconds one particle takes from its place to its new place
    const SETTLE = 0.7;    // seconds after the last particle lands, before the picture takes over

    const wgslCommon = /* wgsl */ `
        struct U { time: f32, dt: f32, columns: f32, rows: f32, cell: f32, ox: f32, oy: f32, span: f32, width: f32, height: f32, fade: f32, pad: f32 };
        struct S { src: vec2f, dst: vec2f, c0: u32, c1: u32, seed: f32, delay: f32 };
        struct P { pos: vec2f, vel: vec2f };
        fn progress(s: S) -> f32 { return clamp((u.time - s.delay) / u.span, 0.0, 1.0); }
        // A curved flight: a cubic Bézier that bows sideways and lifts, different for each particle.
        fn path(s: S, t: f32) -> vec2f {
            let e = t * t * (3.0 - 2.0 * t);
            let d = s.dst - s.src;
            let side = vec2f(-d.y, d.x) * (fract(s.seed * 7.13) - 0.5) * 1.4;
            let lift = vec2f(0.0, -u.rows * 0.18 * (0.4 + fract(s.seed * 3.7)));
            let c1 = s.src + d * 0.25 + side * 0.6 + lift;
            let c2 = s.src + d * 0.75 - side * 0.3 + lift * 0.5;
            let a = 1.0 - e;
            return a * a * a * s.src + 3.0 * a * a * e * c1 + 3.0 * a * e * e * c2 + e * e * e * s.dst;
        }`;

    const wgslStep = /* wgsl */ `
        @group(0) @binding(0) var<uniform> u: U;
        @group(0) @binding(1) var<storage, read> statics: array<S>;
        @group(0) @binding(2) var<storage, read_write> particles: array<P>;
        ${wgslCommon}
        @compute @workgroup_size(256)
        fn main(@builtin(global_invocation_id) id: vec3u) {
            let i = id.x;
            if (i >= arrayLength(&statics)) { return; }
            let s = statics[i];
            var p = particles[i];
            let t = progress(s);
            let wob = sin(3.14159 * t);
            let q = p.pos * 0.11 + vec2f(u.time * 1.3, -u.time);
            let swirl = vec2f(sin(q.y * 1.7) + sin(q.y * 0.6 + s.seed), cos(q.x * 1.3) + cos(q.x * 0.7 - s.seed));
            let force = (path(s, t) - p.pos) * 95.0 - p.vel * 12.0 + swirl * wob * u.columns * 1.8;
            p.vel += force * u.dt;
            p.pos += p.vel * u.dt;
            particles[i] = p;
        }`;

    const wgslDraw = /* wgsl */ `
        @group(0) @binding(0) var<uniform> u: U;
        @group(0) @binding(1) var<storage, read> statics: array<S>;
        @group(0) @binding(2) var<storage, read> particles: array<P>;
        ${wgslCommon}
        struct V { @builtin(position) at: vec4f, @location(0) tint: vec4f };
        @vertex
        fn vs(@builtin(vertex_index) vi: u32, @builtin(instance_index) ii: u32) -> V {
            var corners = array<vec2f, 6>(vec2f(0, 0), vec2f(1, 0), vec2f(0, 1), vec2f(0, 1), vec2f(1, 0), vec2f(1, 1));
            let s = statics[ii];
            let p = particles[ii];
            let t = progress(s);
            let wob = sin(3.14159 * t);
            let size = 1.12 + 0.8 * wob;
            let px = vec2f(u.ox, u.oy) + (p.pos + (corners[vi] - 0.5) * size) * u.cell;
            var out: V;
            out.at = vec4f(px.x / u.width * 2.0 - 1.0, 1.0 - px.y / u.height * 2.0, 0.0, 1.0);
            var c = mix(unpack4x8unorm(s.c0), unpack4x8unorm(s.c1), smoothstep(0.15, 0.9, t));
            // Fast particles run hot.
            let heat = min(length(p.vel) / u.columns * 0.8, 0.7) * wob;
            let rgb = c.rgb + vec3f(0.4, 0.32, 0.7) * heat;
            out.tint = vec4f(rgb * u.fade, u.fade);
            return out;
        }
        @fragment
        fn fs(v: V) -> @location(0) vec4f { return v.tint; }`;

    const glslDraw = `#version 300 es
        precision highp float;
        in vec2 src; in vec2 dst; in vec4 c0; in vec4 c1; in vec2 seedDelay;
        uniform float time; uniform float span; uniform float columns; uniform float rows; uniform float cell; uniform float fade;
        uniform vec2 origin; uniform vec2 canvasSize;
        out vec4 tint;
        const vec2 corners[6] = vec2[6](vec2(0, 0), vec2(1, 0), vec2(0, 1), vec2(0, 1), vec2(1, 0), vec2(1, 1));
        vec2 path(float t, float seed) {
            float e = t * t * (3.0 - 2.0 * t);
            vec2 d = dst - src;
            vec2 side = vec2(-d.y, d.x) * (fract(seed * 7.13) - 0.5) * 1.4;
            vec2 lift = vec2(0.0, -rows * 0.18 * (0.4 + fract(seed * 3.7)));
            vec2 a1 = src + d * 0.25 + side * 0.6 + lift, a2 = src + d * 0.75 - side * 0.3 + lift * 0.5;
            float a = 1.0 - e;
            return a * a * a * src + 3.0 * a * a * e * a1 + 3.0 * a * e * e * a2 + e * e * e * dst;
        }
        void main() {
            float t = clamp((time - seedDelay.y) / span, 0.0, 1.0);
            float wob = sin(3.14159 * t);
            vec2 p = path(t, seedDelay.x);
            p += vec2(sin(p.y * 0.21 + time * 3.1 + seedDelay.x), cos(p.x * 0.17 - time * 2.7 + seedDelay.x)) * wob * columns * 0.03;
            float size = 1.12 + 0.8 * wob;
            vec2 px = origin + (p + (corners[gl_VertexID] - 0.5) * size) * cell;
            gl_Position = vec4(px.x / canvasSize.x * 2.0 - 1.0, 1.0 - px.y / canvasSize.y * 2.0, 0.0, 1.0);
            vec4 c = mix(c0, c1, smoothstep(0.15, 0.9, t));
            tint = vec4((c.rgb + vec3(0.4, 0.32, 0.7) * wob * 0.3) * fade, fade);
        }`;

    const glslFill = `#version 300 es
        precision highp float; in vec4 tint; out vec4 colour; void main() { colour = tint; }`;

    // Each pixel's sort key: brightness, then hue, then its own index in the low 16 bits.
    function order(data, n) {
        const keys = new Uint32Array(n);
        for (let i = 0; i < n; i++) {
            const r = data[i * 4] / 255, g = data[i * 4 + 1] / 255, b = data[i * 4 + 2] / 255;
            const max = Math.max(r, g, b), min = Math.min(r, g, b);
            let hue = 0;
            if (max > min) {
                hue = max === r ? ((g - b) / (max - min) + 6) % 6 : max === g ? (b - r) / (max - min) + 2 : (r - g) / (max - min) + 4;
                hue /= 6;
            }
            const luma = 0.299 * r + 0.587 * g + 0.114 * b;
            keys[i] = (Math.round(luma * 63) * 64 + Math.round(hue * 63)) * 65536 + i;
        }
        return keys.sort();
    }

    /** Every particle's start, end, colours and timing, 32 bytes each. */
    function particlesFor(from, to, wanted) {
        // At most 65,535 particles: the sort key keeps the index in 16 bits.
        const columns = Math.max(16, Math.min(wanted, Math.floor(Math.sqrt(60000 * to.width / to.height))));
        const rows = Math.max(8, Math.round(columns * to.height / to.width));
        const n = columns * rows;
        const source = window.poFx.pixelsOf(from, columns, rows), target = window.poFx.pixelsOf(to, columns, rows);
        const sourceOrder = order(source, n), targetOrder = order(target, n);
        const buffer = new ArrayBuffer(n * 32);
        const floats = new Float32Array(buffer), bytes = new Uint8Array(buffer);
        for (let k = 0; k < n; k++) {
            const s = sourceOrder[k] & 0xffff, d = targetOrder[k] & 0xffff;
            const sx = s % columns, sy = Math.floor(s / columns), dx = d % columns, dy = Math.floor(d / columns);
            const seed = Math.random() * 100;
            // A wave that sweeps down the picture, loosened by chance.
            const delay = dy / rows * 0.55 + dx / columns * 0.15 + Math.random() * 0.35;
            floats.set([sx + 0.5, sy + 0.5, dx + 0.5, dy + 0.5], k * 8);
            bytes.set([source[s * 4], source[s * 4 + 1], source[s * 4 + 2], 255, target[d * 4], target[d * 4 + 1], target[d * 4 + 2], 255], k * 32 + 16);
            floats[k * 8 + 6] = seed;
            floats[k * 8 + 7] = delay;
        }
        return { buffer, n, columns, rows };
    }

    async function withGpu(canvas, data, layout) {
        const device = await window.poFx.device();
        const context = device && canvas.getContext('webgpu');
        if (!context) return null;
        const format = navigator.gpu.getPreferredCanvasFormat();
        context.configure({ device, format, alphaMode: 'premultiplied' });

        const statics = device.createBuffer({ size: data.buffer.byteLength, usage: GPUBufferUsage.STORAGE | GPUBufferUsage.COPY_DST });
        device.queue.writeBuffer(statics, 0, data.buffer);
        const start = new Float32Array(data.n * 4);
        const floats = new Float32Array(data.buffer);
        for (let i = 0; i < data.n; i++) start.set([floats[i * 8], floats[i * 8 + 1], 0, 0], i * 4);
        const particles = device.createBuffer({ size: start.byteLength, usage: GPUBufferUsage.STORAGE | GPUBufferUsage.COPY_DST });
        device.queue.writeBuffer(particles, 0, start);
        const uniform = device.createBuffer({ size: 48, usage: GPUBufferUsage.UNIFORM | GPUBufferUsage.COPY_DST });

        const stepPipeline = device.createComputePipeline({
            layout: 'auto',
            compute: { module: device.createShaderModule({ code: wgslStep }), entryPoint: 'main' },
        });
        const drawModule = device.createShaderModule({ code: wgslDraw });
        const drawPipeline = device.createRenderPipeline({
            layout: 'auto',
            vertex: { module: drawModule, entryPoint: 'vs' },
            fragment: {
                module: drawModule, entryPoint: 'fs',
                targets: [{ format, blend: { color: { srcFactor: 'one', dstFactor: 'one-minus-src-alpha' }, alpha: { srcFactor: 'one', dstFactor: 'one-minus-src-alpha' } } }],
            },
            primitive: { topology: 'triangle-list' },
        });
        const entries = [{ binding: 0, resource: { buffer: uniform } }, { binding: 1, resource: { buffer: statics } }, { binding: 2, resource: { buffer: particles } }];
        const stepGroup = device.createBindGroup({ layout: stepPipeline.getBindGroupLayout(0), entries });
        const drawGroup = device.createBindGroup({ layout: drawPipeline.getBindGroupLayout(0), entries });
        const values = new Float32Array(12);

        return {
            frame(time, dt, fade) {
                values.set([time, dt, data.columns, data.rows, layout.cell, layout.ox, layout.oy, SPAN, canvas.width, canvas.height, fade, 0]);
                device.queue.writeBuffer(uniform, 0, values);
                const encoder = device.createCommandEncoder();
                const compute = encoder.beginComputePass();
                compute.setPipeline(stepPipeline);
                compute.setBindGroup(0, stepGroup);
                compute.dispatchWorkgroups(Math.ceil(data.n / 256));
                compute.end();
                const pass = encoder.beginRenderPass({
                    colorAttachments: [{ view: context.getCurrentTexture().createView(), loadOp: 'clear', clearValue: { r: 0, g: 0, b: 0, a: 0 }, storeOp: 'store' }],
                });
                pass.setPipeline(drawPipeline);
                pass.setBindGroup(0, drawGroup);
                pass.draw(6, data.n);
                pass.end();
                device.queue.submit([encoder.finish()]);
            },
            dispose() { [statics, particles, uniform].forEach((b) => b.destroy()); },
        };
    }

    function withGl(canvas, data, layout) {
        const gl = canvas.getContext('webgl2', { premultipliedAlpha: true });
        if (!gl) return null;
        const program = window.poFx.program(gl, glslDraw, glslFill);
        const vao = gl.createVertexArray();
        gl.bindVertexArray(vao);
        const buffer = gl.createBuffer();
        gl.bindBuffer(gl.ARRAY_BUFFER, buffer);
        gl.bufferData(gl.ARRAY_BUFFER, data.buffer, gl.STATIC_DRAW);
        for (const [name, size, type, normalised, offset] of [
            ['src', 2, gl.FLOAT, false, 0], ['dst', 2, gl.FLOAT, false, 8], ['c0', 4, gl.UNSIGNED_BYTE, true, 16],
            ['c1', 4, gl.UNSIGNED_BYTE, true, 20], ['seedDelay', 2, gl.FLOAT, false, 24]]) {
            const at = gl.getAttribLocation(program, name);
            gl.enableVertexAttribArray(at);
            gl.vertexAttribPointer(at, size, type, normalised, 32, offset);
            gl.vertexAttribDivisor(at, 1);
        }
        gl.useProgram(program);
        const uniform = (name) => gl.getUniformLocation(program, name);
        gl.uniform1f(uniform('span'), SPAN);
        gl.uniform1f(uniform('columns'), data.columns);
        gl.uniform1f(uniform('rows'), data.rows);
        gl.uniform1f(uniform('cell'), layout.cell);
        gl.uniform2f(uniform('origin'), layout.ox, layout.oy);
        gl.uniform2f(uniform('canvasSize'), canvas.width, canvas.height);
        gl.enable(gl.BLEND);
        gl.blendFunc(gl.ONE, gl.ONE_MINUS_SRC_ALPHA);
        const time = uniform('time'), fade = uniform('fade');
        return {
            frame(t, dt, f) {
                gl.viewport(0, 0, canvas.width, canvas.height);
                gl.clearColor(0, 0, 0, 0);
                gl.clear(gl.COLOR_BUFFER_BIT);
                gl.uniform1f(time, t);
                gl.uniform1f(fade, f);
                gl.drawArraysInstanced(gl.TRIANGLES, 0, 6, data.n);
            },
            dispose() { gl.getExtension('WEBGL_lose_context')?.loseContext(); },
        };
    }

    async function play(figure, fromUrl, toUrl) {
        const picture = figure?.querySelector('img');
        if (!picture || !window.poFx.enabled() || !fromUrl || !toUrl) return;
        picture.style.transition = 'opacity 450ms';
        picture.style.opacity = '0';
        let canvas = null, engine = null;
        try {
            const gpu = await window.poFx.device();
            const [from, to] = await Promise.all([window.poFx.bitmap(fromUrl), window.poFx.bitmap(toUrl)]);
            const data = particlesFor(from, to, gpu ? 230 : 150);
            const natural = { width: to.width, height: to.height };
            from.close();
            to.close();
            await picture.decode().catch(() => { });
            await Promise.all(figure.getAnimations({ subtree: true }).map((a) => a.finished)).catch(() => { });

            // Where the picture is drawn inside its box (object-fit: contain), relative to the figure.
            const box = picture.getBoundingClientRect(), frame = figure.getBoundingClientRect();
            const ratio = Math.min(box.width / natural.width, box.height / natural.height);
            const width = natural.width * ratio, height = natural.height * ratio, margin = 70;
            canvas = document.createElement('canvas');
            canvas.className = 'po-morph';
            canvas.setAttribute('aria-hidden', 'true');
            Object.assign(canvas.style, {
                left: `${box.left - frame.left + (box.width - width) / 2 - margin}px`,
                top: `${box.top - frame.top + (box.height - height) / 2 - margin}px`,
                width: `${width + margin * 2}px`,
                height: `${height + margin * 2}px`,
            });
            figure.appendChild(canvas);
            const density = window.poFx.fit(canvas, 2);
            const layout = { cell: width * density / data.columns, ox: margin * density, oy: margin * density };
            engine = (gpu && await withGpu(canvas, data, layout).catch((err) => { console.warn('[poMorph] WebGPU', err); return null; }))
                ?? withGl(canvas, data, layout);
            if (!engine) throw new Error('no GPU canvas');

            window.poSfx?.playAt('shimmer', figure);
            const lastLanding = 0.55 + 0.15 + 0.35 + SPAN;
            await new Promise((resolve) => {
                let began = 0, previous = 0, revealed = false;
                const tick = (now) => {
                    began ||= now;
                    const time = (now - began) / 1000;
                    const dt = Math.min(0.033, (now - (previous || now)) / 1000) || 0.016;
                    previous = now;
                    const fadeStart = lastLanding + SETTLE;
                    const fade = time < fadeStart ? 1 : Math.max(0, 1 - (time - fadeStart) / 0.45);
                    if (!revealed && time >= fadeStart) { revealed = true; picture.style.opacity = '1'; }
                    engine.frame(time, dt, fade);
                    if (fade <= 0 || !canvas.isConnected) resolve();
                    else requestAnimationFrame(tick);
                };
                requestAnimationFrame(tick);
            });
        } catch (err) {
            console.warn('[poMorph]', err?.message ?? err);
        } finally {
            picture.style.opacity = '1';
            engine?.dispose();
            canvas?.remove();
        }
    }

    return {
        /** Rebuilds the picture in `figure` from the one at `fromUrl`. */
        play(figure, fromUrl, toUrl) {
            // A batch of results reveals its first few; the rest simply appear.
            if (running.size >= 3) return Promise.resolve();
            const run = play(figure, fromUrl, toUrl);
            running.add(run);
            run.finally(() => running.delete(run));
            return run;
        },

        /** For .NET: starts the reveal for the figure matching `selector` without waiting for it. */
        start(selector, fromUrl, toUrl) {
            window.poMorph.play(document.querySelector(selector), fromUrl, toUrl);
        },

        /** Resolves once every reveal now playing has finished (or after six seconds). */
        settled() {
            return Promise.race([Promise.all([...running]), new Promise((resolve) => setTimeout(resolve, 6000))]);
        },
    };
})();
