// The run reactor: a raymarched orb that shows a run's state. Each function in the run is a blob
// orbiting a glowing core; when a step finishes its blob is drawn into the core (the distance
// fields are joined with a smooth minimum, so they melt together), and the core grows and burns
// brighter as the run progresses. The AI's cue lines ("HIT #", "ROAST @") make it flare.
// A failed run cracks, glitches and greys; a cancelled one implodes; a finished one flashes and
// settles. Each finished step also plays the next note up (poSfx.step).
window.poReactor = (() => {
    const MAX_BLOBS = 8;

    const fragment = `#version 300 es
        precision highp float;
        uniform vec2 size; uniform float time; uniform int count; uniform float done; uniform float now;
        uniform float percent; uniform float flare; uniform float broken; uniform float implode; uniform float complete;
        uniform vec3 a; uniform vec3 b;
        out vec4 colour;

        float hash(vec2 p) { return fract(sin(dot(p, vec2(127.1, 311.7))) * 43758.5453); }
        float smin(float x, float y, float k) { float h = clamp(0.5 + 0.5 * (y - x) / k, 0.0, 1.0); return mix(y, x, h) - k * h * (1.0 - h); }

        // Blob i: where it orbits, and how big it is. A finished blob slides into the core.
        vec4 blob(int i) {
            float fi = float(i), n = float(max(count, 1));
            float absorbed = clamp(done - fi, 0.0, 1.0);
            float current = 1.0 - clamp(abs(fi - now), 0.0, 1.0);
            float angle = time * (0.55 + 0.12 * fi) + fi * 6.2831 / n;
            float orbit = mix(1.08 + current * 0.1, 0.0, absorbed * absorbed);
            vec3 p = vec3(cos(angle) * orbit, sin(angle * 1.3 + fi) * 0.32 * orbit, sin(angle) * orbit);
            float r = mix(0.2 + current * (0.07 + 0.03 * sin(time * 7.0)), 0.0, absorbed);
            return vec4(p, r);
        }

        float map(vec3 p) {
            p /= max(0.02, 1.0 - implode);
            float n = float(max(count, 1));
            float core = 0.4 + 0.16 * done / n + flare * 0.07 + 0.015 * sin(time * 2.0) + complete * 0.05;
            float ripple = 0.025 * (1.0 + percent) * sin(p.x * 9.0 + time * 3.0) * sin(p.y * 8.0 + time * 2.0) * sin(p.z * 7.0 - time);
            float d = length(p) - core + ripple;
            for (int i = 0; i < ${MAX_BLOBS}; i++) {
                if (i >= count) break;
                vec4 o = blob(i);
                if (o.w > 0.001) d = smin(d, length(p - o.xyz) - o.w, 0.32);
            }
            return d * max(0.02, 1.0 - implode);
        }

        vec3 normalAt(vec3 p) {
            vec2 e = vec2(0.002, 0.0);
            return normalize(vec3(map(p + e.xyy) - map(p - e.xyy), map(p + e.yxy) - map(p - e.yxy), map(p + e.yyx) - map(p - e.yyx)));
        }

        // Cells on the surface; their borders are the cracks of a broken run.
        float cracks(vec3 p) {
            vec2 q = vec2(atan(p.z, p.x) * 2.0, p.y * 5.0);
            vec2 i = floor(q), f = fract(q);
            float first = 8.0, second = 8.0;
            for (int y = -1; y <= 1; y++) for (int x = -1; x <= 1; x++) {
                vec2 g = vec2(x, y);
                vec2 o = vec2(hash(i + g), hash(i + g + 17.0));
                float d = length(g + o - f);
                if (d < first) { second = first; first = d; } else if (d < second) second = d;
            }
            return 1.0 - smoothstep(0.0, 0.06, second - first);
        }

        void main() {
            vec2 uv = (gl_FragCoord.xy - size * 0.5) / min(size.x, size.y) * 1.25;
            // A broken run tears into horizontal bands that slip sideways.
            float band = floor(uv.y * 18.0 + floor(time * 9.0));
            uv.x += broken * (hash(vec2(band, floor(time * 9.0))) - 0.5) * 0.12 * step(0.72, hash(vec2(band, 3.0)));

            vec3 eye = vec3(0.0, 0.0, 3.1), ray = normalize(vec3(uv, -1.6));
            float travelled = 0.0, closest = 9.0;
            bool hit = false;
            for (int i = 0; i < 72; i++) {
                vec3 p = eye + ray * travelled;
                float d = map(p);
                closest = min(closest, d);
                if (d < 0.001) { hit = true; break; }
                travelled += d * 0.9;
                if (travelled > 6.0) break;
            }

            vec3 hot = mix(a, b, 0.5 + 0.5 * sin(time * 0.7));
            float heat = 0.35 + percent * 0.65 + flare * 0.8 + complete * 0.6;
            // The halo: light that leaks out around the surface.
            float halo = exp(-max(closest, 0.0) * 7.0) * (0.35 + heat * 0.4);
            vec3 result = hot * halo;
            float alpha = clamp(halo, 0.0, 1.0);

            if (hit) {
                vec3 p = eye + ray * travelled, n = normalAt(p), view = -ray;
                float fresnel = pow(1.0 - max(dot(n, view), 0.0), 3.0);
                // Thin-film iridescence: hue that shifts with the viewing angle.
                vec3 film = 0.5 + 0.5 * cos(6.2831 * (fresnel * 1.3 + vec3(0.0, 0.33, 0.67)) + time * 0.6);
                vec3 light = normalize(vec3(0.6, 0.8, 0.9));
                float diffuse = max(dot(n, light), 0.0);
                float specular = pow(max(dot(reflect(-light, n), view), 0.0), 40.0);
                vec3 body = mix(a * 0.5, b, diffuse * 0.6 + 0.2) + hot * heat * 0.5 * (1.0 - fresnel);
                result = body + film * fresnel * 0.9 + vec3(specular) * 0.8 + vec3(1.0) * complete * 0.5;
                float c = cracks(p) * broken;
                float grey = dot(result, vec3(0.299, 0.587, 0.114));
                result = mix(result, vec3(grey * 0.6), broken * 0.85) + vec3(1.0, 0.25, 0.15) * c * 2.0;
                alpha = 1.0;
            }
            colour = vec4(result * alpha, alpha);
        }`;

    const vertex = `#version 300 es
        in vec2 p; void main() { gl_Position = vec4(p, 0.0, 1.0); }`;

    function init(canvas) {
        const gl = canvas.getContext('webgl2', { premultipliedAlpha: true, antialias: false });
        if (!gl) return null;
        let program;
        try {
            program = window.poFx.program(gl, vertex, fragment);
        } catch (err) {
            console.warn('[poReactor]', err.message);
            return null;
        }
        gl.useProgram(program);
        gl.bindBuffer(gl.ARRAY_BUFFER, gl.createBuffer());
        gl.bufferData(gl.ARRAY_BUFFER, new Float32Array([-1, -1, 3, -1, -1, 3]), gl.STATIC_DRAW);
        const at = gl.getAttribLocation(program, 'p');
        gl.enableVertexAttribArray(at);
        gl.vertexAttribPointer(at, 2, gl.FLOAT, false, 0, 0);
        const uniforms = {};
        for (const name of ['size', 'time', 'count', 'done', 'now', 'percent', 'flare', 'broken', 'implode', 'complete', 'a', 'b']) {
            uniforms[name] = gl.getUniformLocation(program, name);
        }
        gl.uniform3fv(uniforms.a, window.poFx.colour('--po-brand-a', '#4340d2'));
        gl.uniform3fv(uniforms.b, window.poFx.colour('--po-brand-b', '#7c3aed'));
        return {
            gl, uniforms,
            target: { count: 1, done: 0, now: 0, percent: 0, status: 'Queued', cancelled: false },
            shown: { done: 0, percent: 0, flare: 0, broken: 0, implode: 0, complete: 0 },
            cue: null, began: performance.now(), frame: 0, last: 0,
        };
    }

    function draw(canvas, state, now) {
        const { gl, uniforms, target, shown } = state;
        const dt = Math.min(0.05, (now - (state.last || now)) / 1000);
        state.last = now;
        const ease = (key, value, rate) => { shown[key] += (value - shown[key]) * Math.min(1, dt * rate); };
        ease('done', target.done, 2.5);
        ease('percent', target.percent / 100, 2);
        shown.flare = Math.max(0, shown.flare - dt * 1.6);
        ease('broken', target.status === 'Failed' && !target.cancelled ? 1 : 0, 3);
        ease('implode', target.status === 'Failed' && target.cancelled ? 1 : 0, 1.6);
        if (target.status === 'Complete') shown.complete = Math.max(0, shown.complete - dt * 0.8);

        window.poFx.fit(canvas, 1.5);
        gl.viewport(0, 0, canvas.width, canvas.height);
        gl.clearColor(0, 0, 0, 0);
        gl.clear(gl.COLOR_BUFFER_BIT);
        gl.uniform2f(uniforms.size, canvas.width, canvas.height);
        gl.uniform1f(uniforms.time, (now - state.began) / 1000);
        gl.uniform1i(uniforms.count, Math.min(MAX_BLOBS, target.count));
        gl.uniform1f(uniforms.done, shown.done);
        gl.uniform1f(uniforms.now, target.now);
        gl.uniform1f(uniforms.percent, shown.percent);
        gl.uniform1f(uniforms.flare, shown.flare);
        gl.uniform1f(uniforms.broken, shown.broken);
        gl.uniform1f(uniforms.implode, shown.implode);
        gl.uniform1f(uniforms.complete, shown.complete);
        gl.drawArrays(gl.TRIANGLES, 0, 3);
    }

    function loop(canvas) {
        const state = canvas._poReactor;
        const tick = (now) => {
            if (!canvas.isConnected) {
                state.gl.getExtension('WEBGL_lose_context')?.loseContext();
                delete canvas._poReactor;
                return;
            }
            draw(canvas, state, now);
            // A settled orb (finished a while ago, or imploded) stops drawing.
            const resting = (state.target.status === 'Complete' && state.shown.complete < 0.01 && now - state.endedAt > 6000)
                || (state.target.cancelled && state.shown.implode > 0.995);
            state.frame = resting || document.hidden ? 0 : requestAnimationFrame(tick);
        };
        if (!state.frame) state.frame = requestAnimationFrame(tick);
    }

    return {
        /**
         * The run's state, sent on every change: how many functions, how many are done, which
         * one is working (-1 for none), percent, status, whether it was cancelled, and the newest
         * log line (a cue line makes the orb flare).
         */
        update(canvas, count, done, now, percent, status, cancelled, latest) {
            if (!canvas || !window.poFx.enabled()) return;
            canvas._poReactor ??= init(canvas);
            const state = canvas._poReactor;
            if (!state) { canvas.hidden = true; return; }
            const previous = state.target;
            const first = previous.status === 'Queued' && state.cue === null;

            // Each newly finished step plays the next note up the scale.
            if (!first && done > previous.done && status !== 'Failed') {
                for (let n = previous.done; n < done; n++) setTimeout(() => window.poSfx?.step(n), (n - previous.done) * 140);
            }
            if (latest && latest !== state.cue && state.cue !== null && /^(HIT #|ROAST @)/.test(latest)) {
                state.shown.flare = 1;
                window.poSfx?.playAt('cue', canvas);
            }
            if (status === 'Complete' && previous.status !== 'Complete') {
                state.shown.complete = first ? 0 : 1;
                state.endedAt = performance.now();
            }
            state.cue = latest ?? '';
            state.target = { count, done, now, percent, status, cancelled };
            loop(canvas);
        },
    };
})();
