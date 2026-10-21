// The drop zone's gravity well: tens of thousands of particles, simulated on the GPU with WebGL2
// transform feedback (each frame a vertex shader reads every particle's position and velocity
// and writes the next ones into a second buffer; the two buffers swap).
//
//   attract  — a file is dragged over the page: particles swirl into the pointer like an
//              accretion disk, pulled in and spun round, stirred by curl noise.
//   collapse — the file is dropped: everything falls into that point, then a shockwave throws
//              it back out and it fades.
//   stream   — the file is uploading: particles pour in from the edges of the screen into the
//              drop zone, faster as the upload goes on.
window.poParticles = (() => {
    const update = `#version 300 es
        precision highp float;
        in vec2 position; in vec2 velocity; in vec2 meta;
        uniform float dt; uniform float time; uniform vec2 screen; uniform vec2 well;
        uniform float pull; uniform float swirl; uniform float burst; uniform float stream;
        out vec2 nextPosition; out vec2 nextVelocity; out vec2 nextMeta;

        float hash(float n) { return fract(sin(n) * 43758.5453123); }
        float noise(vec2 p) {
            vec2 i = floor(p), f = fract(p);
            vec2 u = f * f * (3.0 - 2.0 * f);
            float a = hash(dot(i, vec2(1.0, 57.0))), b = hash(dot(i + vec2(1.0, 0.0), vec2(1.0, 57.0)));
            float c = hash(dot(i + vec2(0.0, 1.0), vec2(1.0, 57.0))), d = hash(dot(i + vec2(1.0, 1.0), vec2(1.0, 57.0)));
            return mix(mix(a, b, u.x), mix(c, d, u.x), u.y);
        }
        // The curl of a noise field: a flow with no sources or sinks, so it stirs without clumping.
        vec2 curl(vec2 p) {
            float e = 0.1;
            float dx = noise(p + vec2(e, 0.0)) - noise(p - vec2(e, 0.0));
            float dy = noise(p + vec2(0.0, e)) - noise(p - vec2(0.0, e));
            return vec2(dy, -dx) / (2.0 * e);
        }
        // The pull toward the well at distance r.
        float gravity(float r) { return pull * 7000.0 / sqrt(r); }

        // A new particle: somewhere on a disk around the well, or on the edge of the screen (stream).
        vec2 spawn(float seed) {
            float r1 = hash(seed + time * 13.1), r2 = hash(seed * 1.7 + time * 7.3);
            if (stream < 0.5) {
                float angle = r1 * 6.2831853;
                float radius = 40.0 + pow(r2, 0.7) * min(screen.x, screen.y) * 0.5;
                return well + vec2(cos(angle), sin(angle)) * radius;
            }
            float side = floor(hash(seed * 3.1 + time) * 4.0);
            if (side < 1.0) return vec2(r1 * screen.x, -8.0);
            if (side < 2.0) return vec2(r1 * screen.x, screen.y + 8.0);
            if (side < 3.0) return vec2(-8.0, r2 * screen.y);
            return vec2(screen.x + 8.0, r2 * screen.y);
        }

        void main() {
            vec2 p = position, v = velocity;
            float seed = meta.x, life = meta.y;
            vec2 d = well - p;
            float r = length(d) + 1.0;
            vec2 toward = d / r;
            vec2 around = vec2(-toward.y, toward.x) * (hash(seed) < 0.85 ? 1.0 : -1.0);
            vec2 force = toward * gravity(r) + around * swirl * 1500.0 / sqrt(r);
            force += curl(p * 0.004 + vec2(time * 0.05, 0.0)) * 260.0;
            force -= toward * burst * 4.0e6 / (r + 40.0);
            v += force * dt;
            v *= exp(-dt * 0.8);
            p += v * dt;
            life -= dt * (0.12 + hash(seed * 5.3) * 0.1);

            bool lost = p.x < -200.0 || p.y < -200.0 || p.x > screen.x + 200.0 || p.y > screen.y + 200.0;
            if (life <= 0.0 || (burst <= 0.0 && r < 7.0) || (lost && burst <= 0.0)) {
                p = spawn(seed);
                // Launched into orbit, so the disk turns from its first frame and spirals in.
                vec2 inward = well - p;
                float reach = length(inward) + 1.0;
                vec2 side = vec2(-inward.y, inward.x) / reach * (hash(seed) < 0.85 ? 1.0 : -1.0);
                v = stream < 0.5 ? side * sqrt(gravity(reach) * reach) * 0.92 : vec2(0.0);
                life = 0.6 + hash(seed + time) * 0.8;
            }
            nextPosition = p; nextVelocity = v; nextMeta = vec2(seed, life);
        }`;

    const render = `#version 300 es
        precision highp float;
        in vec2 position; in vec2 velocity; in vec2 meta;
        uniform vec2 screen; uniform float density; uniform vec3 a; uniform vec3 b; uniform float alpha;
        out vec4 tint;
        float hash(float n) { return fract(sin(n) * 43758.5453123); }
        void main() {
            vec2 clip = position / screen * 2.0 - 1.0;
            gl_Position = vec4(clip.x, -clip.y, 0.0, 1.0);
            float speed = clamp(length(velocity) / 900.0, 0.0, 1.0);
            gl_PointSize = (1.4 + hash(meta.x) * 2.6 + speed * 1.5) * density;
            vec3 c = mix(a, b, hash(meta.x * 7.7));
            c = mix(c, vec3(1.0), speed * 0.55);
            tint = vec4(c, alpha * smoothstep(0.0, 0.25, meta.y) * (0.6 + speed * 0.4));
        }`;

    const dot = `#version 300 es
        precision highp float;
        in vec4 tint; out vec4 colour;
        void main() {
            float d = length(gl_PointCoord - 0.5);
            if (d > 0.5) discard;
            colour = vec4(tint.rgb, tint.a * smoothstep(0.5, 0.1, d));
        }`;

    let gl = null, canvas = null, stepper, painter, buffers = [], arrays = [], count = 0, current = 0;
    let frame = 0, last = 0, time = 0, broken = false;
    const state = { mode: 'off', well: [0, 0], pull: 0, swirl: 0, burst: 0, alpha: 0, alphaTarget: 0, since: 0, releaseTimer: 0, zone: null, progress: 0, lastSeen: 0, dark: false, colours: [[0.26, 0.25, 0.82], [0.49, 0.23, 0.93]] };
    let uniforms = {};

    function init() {
        if (gl || broken) return Boolean(gl);
        canvas = window.poFx.layer('particles', 1090);
        gl = canvas.getContext('webgl2', { premultipliedAlpha: false, antialias: false });
        if (!gl) { broken = true; return false; }
        try {
            stepper = window.poFx.program(gl, update, `#version 300 es
                precision highp float; out vec4 c; void main() { c = vec4(0.0); }`, ['nextPosition', 'nextVelocity', 'nextMeta']);
            painter = window.poFx.program(gl, render, dot);
        } catch (err) {
            console.warn('[poParticles]', err.message);
            broken = true;
            gl = null;
            return false;
        }

        window.poFx.fit(canvas, 1.5);
        count = canvas.clientWidth > 900 ? 56000 : 20000;
        const data = new Float32Array(count * 6);
        for (let i = 0; i < count; i++) {
            data.set([0, 0, 0, 0, i * 0.618 + 1, 0], i * 6);
        }
        for (let k = 0; k < 2; k++) {
            buffers[k] = gl.createBuffer();
            gl.bindBuffer(gl.ARRAY_BUFFER, buffers[k]);
            gl.bufferData(gl.ARRAY_BUFFER, data, gl.DYNAMIC_COPY);
        }
        // One vertex array per program per buffer: the update reads one buffer and writes the other.
        for (const [name, prog] of [['step', stepper], ['paint', painter]]) {
            arrays[name] = buffers.map((buffer) => {
                const vao = gl.createVertexArray();
                gl.bindVertexArray(vao);
                gl.bindBuffer(gl.ARRAY_BUFFER, buffer);
                [['position', 0], ['velocity', 8], ['meta', 16]].forEach(([attr, offset]) => {
                    const at = gl.getAttribLocation(prog, attr);
                    if (at < 0) return;
                    gl.enableVertexAttribArray(at);
                    gl.vertexAttribPointer(at, 2, gl.FLOAT, false, 24, offset);
                });
                return vao;
            });
        }
        gl.bindVertexArray(null);
        // A buffer may not be bound anywhere else while transform feedback writes to it.
        gl.bindBuffer(gl.ARRAY_BUFFER, null);
        for (const name of ['dt', 'time', 'screen', 'well', 'pull', 'swirl', 'burst', 'stream']) uniforms[`s.${name}`] = gl.getUniformLocation(stepper, name);
        for (const name of ['screen', 'density', 'a', 'b', 'alpha']) uniforms[`p.${name}`] = gl.getUniformLocation(painter, name);
        return true;
    }

    function step(now) {
        const dt = Math.min(0.033, (now - (last || now)) / 1000) || 0.016;
        last = now;
        time += dt;
        state.since += dt;

        // Each mode's forces over time.
        if (state.mode === 'attract') {
            state.pull = 0.9; state.swirl = 1.0; state.burst = 0;
            // A drag repeats dragover many times a second. When they stop without a leave or a
            // drop (cancelled with Escape, dropped outside the window), the well fades anyway.
            if (now - state.lastSeen > 400) { state.alphaTarget = 0; window.poSfx?.whoosh(0); }
        } else if (state.mode === 'stream') {
            if (now - state.lastSeen > 4000) state.alphaTarget = 0;
            if (state.zone?.isConnected) {
                const r = state.zone.getBoundingClientRect();
                state.well = [r.left + r.width / 2, r.top + r.height / 2];
            }
            state.pull = 0.7 + state.progress * 1.4; state.swirl = 0.5; state.burst = 0;
        } else if (state.mode === 'collapse') {
            state.pull = state.since < 0.28 ? 5 : 0;
            state.swirl = state.since < 0.28 ? 0.4 : 0;
            state.burst = state.since >= 0.28 && state.since < 0.36 ? 1 : 0;
            if (state.since > 0.36) state.alphaTarget = 0;
        } else {
            state.pull = 0.2; state.swirl = 0.2; state.burst = 0;
        }
        // Time-based, so a slow device fades out as quickly as a fast one.
        state.alpha += (state.alphaTarget - state.alpha) * (1 - Math.exp(-dt * (state.alphaTarget > state.alpha ? 8 : 4)));

        const density = window.poFx.fit(canvas, 1.5);
        const w = canvas.clientWidth, h = canvas.clientHeight;

        gl.useProgram(stepper);
        gl.uniform1f(uniforms['s.dt'], dt);
        gl.uniform1f(uniforms['s.time'], time);
        gl.uniform2f(uniforms['s.screen'], w, h);
        gl.uniform2f(uniforms['s.well'], state.well[0], state.well[1]);
        gl.uniform1f(uniforms['s.pull'], state.pull);
        gl.uniform1f(uniforms['s.swirl'], state.swirl);
        gl.uniform1f(uniforms['s.burst'], state.burst);
        gl.uniform1f(uniforms['s.stream'], state.mode === 'stream' ? 1 : 0);
        gl.bindVertexArray(arrays.step[current]);
        gl.bindBufferBase(gl.TRANSFORM_FEEDBACK_BUFFER, 0, buffers[1 - current]);
        gl.enable(gl.RASTERIZER_DISCARD);
        gl.beginTransformFeedback(gl.POINTS);
        gl.drawArrays(gl.POINTS, 0, count);
        gl.endTransformFeedback();
        gl.disable(gl.RASTERIZER_DISCARD);
        gl.bindBufferBase(gl.TRANSFORM_FEEDBACK_BUFFER, 0, null);
        current = 1 - current;

        gl.viewport(0, 0, canvas.width, canvas.height);
        gl.clearColor(0, 0, 0, 0);
        gl.clear(gl.COLOR_BUFFER_BIT);
        gl.useProgram(painter);
        gl.uniform2f(uniforms['p.screen'], w, h);
        gl.uniform1f(uniforms['p.density'], density);
        gl.uniform3fv(uniforms['p.a'], state.colours[0]);
        gl.uniform3fv(uniforms['p.b'], state.colours[1]);
        gl.uniform1f(uniforms['p.alpha'], state.alpha);
        gl.enable(gl.BLEND);
        // Light adds up to a glow on a dark page; on a light page it would wash out to white.
        if (state.dark) gl.blendFunc(gl.SRC_ALPHA, gl.ONE);
        else gl.blendFunc(gl.SRC_ALPHA, gl.ONE_MINUS_SRC_ALPHA);
        gl.bindVertexArray(arrays.paint[current]);
        gl.drawArrays(gl.POINTS, 0, count);
        gl.bindVertexArray(null);

        const done = state.alphaTarget === 0 && state.alpha < 0.01;
        if (done) {
            state.mode = 'off';
            gl.clear(gl.COLOR_BUFFER_BIT);
            frame = 0;
            last = 0;
            return;
        }
        frame = requestAnimationFrame(step);
    }

    function run() {
        if (!frame) frame = requestAnimationFrame(step);
    }

    function begin(mode) {
        if (!window.poFx.enabled() || !init()) return false;
        clearTimeout(state.releaseTimer);
        if (state.mode === 'off') {
            state.dark = window.poFx.dark();
            state.colours = [window.poFx.colour('--po-brand-a', '#4340d2'), state.dark ? [0.55, 0.85, 1] : window.poFx.colour('--po-brand-b', '#7c3aed')];
        }
        if (state.mode !== mode) state.since = 0;
        state.mode = mode;
        state.lastSeen = performance.now();
        state.alphaTarget = mode === 'collapse' ? 1 : 0.85;
        run();
        return true;
    }

    let lastPointer = null, lastPointerAt = 0;

    return {
        /** A file is being dragged at (x, y). */
        attract(x, y) {
            if (!begin('attract')) return;
            // How fast the pointer moves drives the rush of air.
            const now = performance.now();
            if (lastPointer) {
                const speed = Math.hypot(x - lastPointer[0], y - lastPointer[1]) / Math.max(1, now - lastPointerAt);
                window.poSfx?.whoosh(Math.min(1, 0.2 + speed * 0.6));
            }
            lastPointer = [x, y];
            lastPointerAt = now;
            state.well = [x, y];
        },

        /** The drag left. Waits a moment, since moving between a zone's children also "leaves". */
        release() {
            clearTimeout(state.releaseTimer);
            state.releaseTimer = setTimeout(() => {
                if (state.mode === 'attract') state.alphaTarget = 0;
                lastPointer = null;
                window.poSfx?.whoosh(0);
            }, 140);
        },

        /** Dropped at (x, y), or on the centre of an element. */
        collapse(x, y) {
            if (typeof x === 'object' && x) {
                const r = x.getBoundingClientRect();
                [x, y] = [r.left + r.width / 2, r.top + r.height / 2];
            }
            window.poSfx?.whoosh(0);
            lastPointer = null;
            if (!begin('collapse')) return;
            state.since = 0;
            state.well = [x, y];
            setTimeout(() => { window.poSfx?.play('drop', x / window.innerWidth * 1.6 - 0.8); window.poFx.haptic(18); }, 280);
        },

        /** An upload into `zone` is `fraction` done. */
        stream(zone, fraction) {
            if (state.mode === 'collapse' && state.since < 0.6) return;
            if (!begin('stream')) return;
            state.zone = zone;
            state.progress = fraction;
        },

        /** The upload finished: the stream falls into the zone. */
        finish() {
            if (state.mode !== 'stream') return;
            const [x, y] = state.well;
            window.poParticles.collapse(x, y);
        },

        /** Everything stops, gently. */
        stop() {
            state.alphaTarget = 0;
            window.poSfx?.whoosh(0);
        },
    };
})();
