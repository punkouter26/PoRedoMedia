// Concert mode for a roast: while the track plays, a screen opens above the lyrics.
//   — A domain-warped noise plasma in the brand colours: its flow speeds up with the mids and it
//     swells with the bass. Each kick sends a ring out from the centre and punches the picture
//     with chromatic aberration; the highs make sparks twinkle.
//   — The line being delivered drops in letter by letter. Every letter is a little body with
//     gravity, a bouncy floor and a spring that keeps it upright; kicks throw the letters up.
//   — When the track ends, the mic is dropped: it falls, spins, bounces and lands with a thud.
// The track's bass, mids, highs and kicks are worked out ahead of time from a decoded copy, so
// nothing is routed through the audio element and its playback is never touched. Interface
// sounds step back while it plays.
window.poConcert = (() => {
    const vertex = `#version 300 es
        in vec2 p; out vec2 uv; void main() { uv = p * 0.5 + 0.5; gl_Position = vec4(p, 0.0, 1.0); }`;

    const fragment = `#version 300 es
        precision highp float;
        in vec2 uv;
        uniform vec2 size; uniform float time; uniform float bass; uniform float mid; uniform float high;
        uniform float kickAge; uniform float punch; uniform vec3 a; uniform vec3 b;
        out vec4 colour;

        float hash(vec2 p) { return fract(sin(dot(p, vec2(127.1, 311.7))) * 43758.5453); }
        float noise(vec2 p) {
            vec2 i = floor(p), f = fract(p);
            vec2 u = f * f * (3.0 - 2.0 * f);
            return mix(mix(hash(i), hash(i + vec2(1.0, 0.0)), u.x), mix(hash(i + vec2(0.0, 1.0)), hash(i + vec2(1.0, 1.0)), u.x), u.y);
        }
        float fbm(vec2 p) {
            float v = 0.0, amp = 0.5;
            for (int i = 0; i < 4; i++) { v += amp * noise(p); p = p * 2.03 + vec2(1.7, 9.2); amp *= 0.5; }
            return v;
        }

        vec3 scene(vec2 at) {
            vec2 p = (at - 0.5) * vec2(size.x / size.y, 1.0);
            float t = time * (0.18 + mid * 0.7);
            // Domain warping: the noise is looked up at a place moved by more noise.
            vec2 q = vec2(fbm(p * 1.6 + t * 0.3), fbm(p * 1.6 - t * 0.25 + 5.2));
            float f = fbm(p * 2.0 + q * (1.4 + bass * 1.2) + t * 0.2);
            vec3 c = mix(vec3(0.03, 0.02, 0.08), mix(a, b, q.x), smoothstep(0.2, 0.9, f));
            c += vec3(1.0, 0.25, 0.55) * pow(f, 3.0) * (0.35 + bass * 0.9);
            float r = length(p);
            c += vec3(1.0, 0.82, 0.92) * exp(-abs(r - kickAge * 1.7) * 28.0) * exp(-kickAge * 2.6) * 0.9;
            // Sparks: a few cells of a grid light up, more of them as the highs rise.
            vec2 grid = p * 22.0, cell = floor(grid);
            float pick = hash(cell + floor(time * 6.0) * 0.731);
            float spark = step(1.0 - (0.015 + high * 0.09), pick) * exp(-length(fract(grid) - 0.5) * 12.0);
            c += vec3(1.0, 0.95, 0.85) * spark * (0.6 + high);
            return c;
        }

        void main() {
            vec2 away = normalize(uv - 0.5 + 1e-4) * punch * 0.014;
            vec3 c = vec3(scene(uv + away).r, scene(uv).g, scene(uv - away).b);
            vec2 centred = uv - 0.5;
            c *= 1.0 - dot(centred, centred) * 1.1;
            c *= 0.94 + 0.06 * sin(uv.y * size.y * 1.6);
            colour = vec4(c, 1.0);
        }`;

    function init(host) {
        const canvas = document.createElement('canvas');
        const words = document.createElement('div');
        words.className = 'roast-kinetic';
        const props = document.createElement('canvas');
        props.className = 'roast-props';
        host.replaceChildren(canvas, words, props);
        const gl = canvas.getContext('webgl2');
        if (!gl) return null;
        let program;
        try {
            program = window.poFx.program(gl, vertex, fragment);
        } catch (err) {
            console.warn('[poConcert]', err.message);
            return null;
        }
        gl.useProgram(program);
        gl.bindBuffer(gl.ARRAY_BUFFER, gl.createBuffer());
        gl.bufferData(gl.ARRAY_BUFFER, new Float32Array([-1, -1, 3, -1, -1, 3]), gl.STATIC_DRAW);
        const at = gl.getAttribLocation(program, 'p');
        gl.enableVertexAttribArray(at);
        gl.vertexAttribPointer(at, 2, gl.FLOAT, false, 0, 0);
        const uniforms = {};
        for (const name of ['size', 'time', 'bass', 'mid', 'high', 'kickAge', 'punch', 'a', 'b']) uniforms[name] = gl.getUniformLocation(program, name);
        gl.uniform3fv(uniforms.a, window.poFx.colour('--po-brand-a', '#4340d2'));
        gl.uniform3fv(uniforms.b, window.poFx.colour('--po-brand-b', '#7c3aed'));
        return {
            host, canvas, words, props, gl, uniforms,
            bands: null, level: { bass: 0, mid: 0, high: 0 }, kickAge: 9, punch: 0, lastKick: -1,
            letters: [], mic: null, frame: 0, last: 0, began: performance.now(), closeTimer: 0,
        };
    }

    // ── Letters ───────────────────────────────────────────────────────────────────────────
    function setLine(state, text) {
        state.words.replaceChildren();
        state.letters = [...text.toUpperCase()].map((ch, i) => {
            const span = document.createElement('span');
            span.className = ch === ' ' ? 'roast-ch space' : 'roast-ch';
            span.textContent = ch === ' ' ? ' ' : ch;
            state.words.appendChild(span);
            return { span, y: -70 - Math.random() * 90, vy: 0, r: (Math.random() - 0.5) * 40, vr: 0, delay: i * 0.022, age: 0 };
        });
    }

    function moveLetters(state, dt, kick) {
        for (const l of state.letters) {
            l.age += dt;
            if (l.age < l.delay) { l.span.style.opacity = '0'; continue; }
            l.span.style.opacity = '1';
            l.vy += 2600 * dt;
            l.y += l.vy * dt;
            if (l.y > 0) {
                l.y = 0;
                l.vy = Math.abs(l.vy) < 60 ? 0 : -l.vy * 0.38;
            }
            // A torsion spring keeps each letter upright.
            l.vr += (-l.r * 140 - l.vr * 9) * dt;
            l.r += l.vr * dt;
            if (kick && l.y === 0) {
                l.vy = -(140 + Math.random() * 240) * kick;
                l.vr += (Math.random() - 0.5) * 220 * kick;
            }
            l.span.style.transform = `translateY(${l.y.toFixed(1)}px) rotate(${l.r.toFixed(1)}deg)`;
        }
    }

    // ── The mic ───────────────────────────────────────────────────────────────────────────
    function moveMic(state, dt) {
        const m = state.mic;
        const g = state.props.getContext('2d');
        const density = window.poFx.fit(state.props, 2);
        const h = state.props.clientHeight;
        g.setTransform(1, 0, 0, 1, 0, 0);
        g.clearRect(0, 0, state.props.width, state.props.height);
        if (!m) return;
        m.age += dt;
        m.vy += 2200 * dt;
        m.x += m.vx * dt;
        m.y += m.vy * dt;
        m.angle += m.spin * dt;
        const floor = h - 30;
        if (m.y > floor && m.vy > 0) {
            if (!m.landed) {
                m.landed = true;
                window.poSfx?.playAt('micdrop', state.host);
                window.poFx.haptic([30, 30, 60]);
                state.kickAge = 0;
                state.punch = 1.4;
                state.host.classList.remove('shake');
                void state.host.offsetWidth;
                state.host.classList.add('shake');
            }
            m.y = floor;
            m.vy = Math.abs(m.vy) < 90 ? 0 : -m.vy * 0.42;
            m.vx *= 0.75;
            m.spin = m.spin * 0.5 + m.vx * 0.02;
        }
        const alpha = Math.min(1, Math.max(0, (3.2 - m.age) / 0.6));
        g.setTransform(density, 0, 0, density, 0, 0);
        g.globalAlpha = alpha;
        g.translate(m.x, m.y);
        g.rotate(m.angle);
        g.font = '52px "Segoe UI Emoji", "Apple Color Emoji", "Noto Color Emoji", sans-serif';
        g.textAlign = 'center';
        g.textBaseline = 'middle';
        g.fillText('🎤', 0, 0);
        if (alpha <= 0) state.mic = null;
    }

    function frame(state, audio, now) {
        if (!state.host.isConnected) return;
        const dt = Math.min(0.05, (now - (state.last || now)) / 1000) || 0.016;
        state.last = now;
        let kick = 0;
        if (state.bands && !audio.paused) {
            const i = Math.min(state.bands.bass.length - 1, Math.floor(audio.currentTime * state.bands.rate));
            for (const key of ['bass', 'mid', 'high']) state.level[key] += ((state.bands[key][i] || 0) - state.level[key]) * 0.35;
            const k = state.bands.kick[i] || 0;
            if (k > 0.55 && audio.currentTime - state.lastKick > 0.16) {
                kick = Math.min(1.2, k);
                state.lastKick = audio.currentTime;
                state.kickAge = 0;
                state.punch = Math.max(state.punch, kick);
            }
        }
        state.kickAge += dt;
        state.punch = Math.max(0, state.punch - dt * 3.5);
        moveLetters(state, dt, kick);
        moveMic(state, dt);

        const { gl, uniforms, canvas } = state;
        window.poFx.fit(canvas, 1.25);
        gl.viewport(0, 0, canvas.width, canvas.height);
        gl.uniform2f(uniforms.size, canvas.width, canvas.height);
        gl.uniform1f(uniforms.time, (now - state.began) / 1000);
        gl.uniform1f(uniforms.bass, state.level.bass);
        gl.uniform1f(uniforms.mid, state.level.mid);
        gl.uniform1f(uniforms.high, state.level.high);
        gl.uniform1f(uniforms.kickAge, state.kickAge);
        gl.uniform1f(uniforms.punch, state.punch);
        gl.drawArrays(gl.TRIANGLES, 0, 3);

        const busy = !audio.paused || state.mic || state.letters.some((l) => l.y !== 0 || Math.abs(l.r) > 0.2);
        state.frame = busy && state.host.classList.contains('live') ? requestAnimationFrame((t) => frame(state, audio, t)) : 0;
    }

    function run(state, audio) {
        if (!state.frame) state.frame = requestAnimationFrame((t) => frame(state, audio, t));
    }

    return {
        attach(stage, audio) {
            const host = stage?.querySelector('.roast-fx');
            if (!host || !audio || !window.poFx.enabled()) return;
            const state = init(host);
            if (!state) return;
            stage._poConcert = state;
            window.poFx.bands(audio.currentSrc || audio.src).then((bands) => { state.bands = bands; }).catch(() => { });
            state.onPlay = () => {
                clearTimeout(state.closeTimer);
                host.classList.add('live');
                window.poSfx?.duck(true);
                run(state, audio);
            };
            state.onPause = () => {
                window.poSfx?.duck(false);
                if (audio.ended) return;
                state.closeTimer = setTimeout(() => host.classList.remove('live'), 1500);
            };
            state.onEnded = () => {
                window.poSfx?.duck(false);
                setLine(state, 'Mic drop!');
                state.mic = { x: host.clientWidth / 2 + (Math.random() - 0.5) * 60, y: -40, vx: (Math.random() - 0.5) * 120, vy: 0, angle: -0.4, spin: 3 + Math.random() * 2, age: 0, landed: false };
                run(state, audio);
                state.closeTimer = setTimeout(() => host.classList.remove('live'), 4200);
            };
            audio.addEventListener('play', state.onPlay);
            audio.addEventListener('pause', state.onPause);
            audio.addEventListener('ended', state.onEnded);
            state.audio = audio;
        },

        /** The line now being delivered (empty between lines). */
        line(stage, text) {
            const state = stage?._poConcert;
            if (!state) return;
            setLine(state, text || '');
            run(state, state.audio);
        },

        detach(stage) {
            const state = stage?._poConcert;
            if (!state) return;
            cancelAnimationFrame(state.frame);
            clearTimeout(state.closeTimer);
            state.audio.removeEventListener('play', state.onPlay);
            state.audio.removeEventListener('pause', state.onPause);
            state.audio.removeEventListener('ended', state.onEnded);
            window.poSfx?.duck(false);
            state.gl.getExtension('WEBGL_lose_context')?.loseContext();
            delete stage._poConcert;
        },
    };
})();
