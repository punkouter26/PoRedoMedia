// Confetti for a finished run, in the colours of what it made: the result's main colours come
// from k-means on a tiny copy of it, so every celebration looks different. Pieces are drawn as
// GPU instances (WebGL2), each a card spinning in 3D: it narrows as it turns edge-on and shows a
// darker back, and it flutters sideways as it falls. Phones also get a short buzz.
window.poConfetti = (() => {
    const vertex = `#version 300 es
        precision highp float;
        in vec2 at; in vec2 size; in float spin; in float flip; in vec3 tint; in float alpha; in float roundness;
        uniform vec2 screen;
        out vec3 colour; out float fade; out vec2 local; out float circle;
        const vec2 corners[6] = vec2[6](vec2(-1, -1), vec2(1, -1), vec2(-1, 1), vec2(-1, 1), vec2(1, -1), vec2(1, 1));
        void main() {
            vec2 c = corners[gl_VertexID];
            local = c;
            vec2 p = c * size * vec2(1.0, max(0.08, abs(flip)));
            float s = sin(spin), k = cos(spin);
            p = vec2(p.x * k - p.y * s, p.x * s + p.y * k);
            vec2 clip = (at + p) / screen * 2.0 - 1.0;
            gl_Position = vec4(clip.x, -clip.y, 0.0, 1.0);
            // The back of a piece is darker, and either side is lit by how square-on it faces.
            colour = tint * (flip < 0.0 ? 0.62 : 1.0) * (0.72 + 0.28 * abs(flip));
            fade = alpha;
            circle = roundness;
        }`;

    const fragment = `#version 300 es
        precision highp float;
        in vec3 colour; in float fade; in vec2 local; in float circle;
        out vec4 result;
        void main() {
            if (circle > 0.5 && dot(local, local) > 1.0) discard;
            result = vec4(colour * fade, fade);
        }`;

    const FLOATS = 12;
    let gl, canvas, program, buffer, vao, uniforms = {}, pieces = [], frame = 0, last = 0, broken = false;

    function init() {
        if (gl || broken) return Boolean(gl);
        canvas = window.poFx.layer('confetti', 1200);
        gl = canvas.getContext('webgl2', { premultipliedAlpha: true, antialias: true });
        try {
            if (!gl) throw new Error('no WebGL2');
            program = window.poFx.program(gl, vertex, fragment);
        } catch (err) {
            console.warn('[poConfetti]', err.message);
            broken = true;
            gl = null;
            return false;
        }
        vao = gl.createVertexArray();
        gl.bindVertexArray(vao);
        buffer = gl.createBuffer();
        gl.bindBuffer(gl.ARRAY_BUFFER, buffer);
        for (const [name, size, offset] of [['at', 2, 0], ['size', 2, 2], ['spin', 1, 4], ['flip', 1, 5], ['tint', 3, 6], ['alpha', 1, 9], ['roundness', 1, 10]]) {
            const at = gl.getAttribLocation(program, name);
            gl.enableVertexAttribArray(at);
            gl.vertexAttribPointer(at, size, gl.FLOAT, false, FLOATS * 4, offset * 4);
            gl.vertexAttribDivisor(at, 1);
        }
        uniforms.screen = gl.getUniformLocation(program, 'screen');
        return true;
    }

    function step(now) {
        const dt = Math.min(0.033, (now - (last || now)) / 1000) || 0.016;
        last = now;
        const w = canvas.clientWidth, h = canvas.clientHeight;
        const data = new Float32Array(pieces.length * FLOATS);
        let alive = 0;
        for (const p of pieces) {
            p.age += dt;
            p.vy += 1500 * dt;
            // Air: fast pieces slow hard; a falling card drifts and tumbles instead of dropping.
            const drag = Math.exp(-dt * (p.vy > 0 ? 3.2 : 1.1));
            p.vx *= drag;
            p.vy *= p.vy > 0 ? Math.exp(-dt * 2.4) : drag;
            p.x += (p.vx + Math.sin(p.age * p.wobble + p.phase) * 90) * dt;
            p.y += p.vy * dt;
            p.spin += p.spinSpeed * dt;
            p.turn += p.turnSpeed * dt;
            const alpha = Math.max(0, Math.min(1, (p.life - p.age) / 0.8));
            if (alpha <= 0 || p.y > h + 40) continue;
            data.set([p.x, p.y, p.w, p.h, p.spin, Math.cos(p.turn), ...p.colour, alpha, p.round, 0], alive * FLOATS);
            alive++;
        }

        window.poFx.fit(canvas, 2);
        gl.viewport(0, 0, canvas.width, canvas.height);
        gl.clearColor(0, 0, 0, 0);
        gl.clear(gl.COLOR_BUFFER_BIT);
        if (alive) {
            gl.useProgram(program);
            gl.bindVertexArray(vao);
            gl.bindBuffer(gl.ARRAY_BUFFER, buffer);
            gl.bufferData(gl.ARRAY_BUFFER, data.subarray(0, alive * FLOATS), gl.DYNAMIC_DRAW);
            gl.uniform2f(uniforms.screen, w, h);
            gl.enable(gl.BLEND);
            gl.blendFunc(gl.ONE, gl.ONE_MINUS_SRC_ALPHA);
            gl.drawArraysInstanced(gl.TRIANGLES, 0, 6, alive);
        }
        if (!alive) {
            pieces = [];
            frame = 0;
            last = 0;
            return;
        }
        frame = requestAnimationFrame(step);
    }

    // Where a celebration starts: the run's orb or result on the page, else the runs button.
    function origin() {
        const candidates = ['.run-reactor', '.run-result:last-of-type', '.tray'];
        for (const selector of candidates) {
            const element = document.querySelector(selector);
            const r = element?.getBoundingClientRect();
            if (r && r.width > 0 && r.bottom > 0 && r.top < window.innerHeight) return [r.left + r.width / 2, r.top + r.height / 2];
        }
        return [window.innerWidth / 2, window.innerHeight / 3];
    }

    async function celebrate(imageUrl) {
        if (!window.poFx.enabled() || !init()) return;
        const fallback = [window.poFx.colour('--po-brand-a'), window.poFx.colour('--po-brand-b'), [1, 0.82, 0.3], [1, 1, 1], [0.2, 0.85, 0.75]];
        let colours = fallback;
        if (imageUrl) {
            colours = await Promise.race([
                window.poFx.palette(imageUrl, 6).catch(() => fallback),
                new Promise((resolve) => setTimeout(() => resolve(fallback), 1500)),
            ]);
            if (colours.length < 3) colours = [...colours, ...fallback];
        }
        // A picture still rebuilding itself gets its moment first.
        await window.poMorph?.settled();

        const [x, y] = origin();
        const count = window.innerWidth > 900 ? 460 : 240;
        for (let i = 0; i < count; i++) {
            const angle = -Math.PI / 2 + (Math.random() - 0.5) * Math.PI * 0.85;
            const speed = 700 + Math.random() * 900;
            const ribbon = Math.random() < 0.18;
            pieces.push({
                x: x + (Math.random() - 0.5) * 30, y: y + (Math.random() - 0.5) * 20,
                vx: Math.cos(angle) * speed, vy: Math.sin(angle) * speed,
                w: ribbon ? 2.5 : 3 + Math.random() * 4, h: ribbon ? 9 + Math.random() * 6 : 2 + Math.random() * 3.5,
                spin: Math.random() * 6.28, spinSpeed: (Math.random() - 0.5) * 9,
                turn: Math.random() * 6.28, turnSpeed: 4 + Math.random() * 9,
                wobble: 2 + Math.random() * 4, phase: Math.random() * 6.28,
                colour: colours[Math.floor(Math.pow(Math.random(), 1.6) * colours.length)],
                round: Math.random() < 0.15 ? 1 : 0,
                age: 0, life: 2.6 + Math.random() * 1.6,
            });
        }
        window.poSfx?.play('pop', x / window.innerWidth * 1.6 - 0.8);
        window.poFx.haptic([14, 40, 22]);
        if (!frame) frame = requestAnimationFrame(step);
    }

    return {
        /** Celebrates a finished run, in the colours of the picture at `imageUrl` when there is one. */
        burst(imageUrl) {
            celebrate(imageUrl).catch((err) => console.warn('[poConfetti]', err?.message ?? err));
        },
    };
})();
