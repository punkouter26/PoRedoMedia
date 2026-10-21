// The page backdrop: one WebGL2 fragment shader drawing a slow mesh gradient in the theme's
// colours. It brightens while a file is dragged over the page and while a run is working.
// No WebGL2, or a request for reduced motion, leaves the plain page background.
//
// Liquid glass: panels marked .po-glass are made see-through by the stylesheet, and the shader
// draws real glass under each one in the same pass. It finds the panel's rounded edge as a signed
// distance, turns its slope into a surface normal and bends the gradient through it like a thick
// lens, with each colour bent a little differently (chromatic fringes), a rim lit from where the
// pointer is, a soft specular glint and faint caustics.
window.poBackdrop = (() => {
    const MAX_PANELS = 12;

    const vertex = `#version 300 es
        in vec2 p; void main() { gl_Position = vec4(p, 0.0, 1.0); }`;

    const fragment = `#version 300 es
        precision highp float;
        uniform vec2 size; uniform float scale; uniform float time; uniform float energy;
        uniform vec3 base; uniform vec3 a; uniform vec3 b;
        uniform vec4 panels[${MAX_PANELS}]; uniform int count; uniform vec2 pointer; uniform float glass; uniform float dark;
        out vec4 colour;

        float hash(vec2 p) { return fract(sin(dot(p, vec2(127.1, 311.7))) * 43758.5453); }
        float noise(vec2 p) {
            vec2 i = floor(p), f = fract(p);
            vec2 u = f * f * (3.0 - 2.0 * f);
            return mix(mix(hash(i), hash(i + vec2(1.0, 0.0)), u.x), mix(hash(i + vec2(0.0, 1.0)), hash(i + vec2(1.0, 1.0)), u.x), u.y);
        }

        // The gradient itself, at uv, with 'amount' of colour over the page.
        vec3 field(vec2 uv, float t, float amount) {
            float n = noise(uv * 2.2 + vec2(t, -t * 0.7)) * 0.6 + noise(uv * 4.6 - vec2(t * 0.6, t)) * 0.4;
            float glow = smoothstep(0.35, 0.95, n);
            vec3 tint = mix(a, b, uv.x + 0.25 * sin(t * 3.0 + uv.y * 4.0));
            return mix(base, tint, glow * amount);
        }

        // Signed distance to a rounded box centred on the origin: negative inside.
        float box(vec2 p, vec2 extent, float r) {
            vec2 q = abs(p) - extent + r;
            return length(max(q, 0.0)) + min(max(q.x, q.y), 0.0) - r;
        }

        void main() {
            vec2 uv = gl_FragCoord.xy / size;
            float t = time * (0.05 + energy * 0.25);
            // The canvas used to sit at 55% opacity over the page; that is folded in here.
            vec3 result = mix(base, field(uv, t, 0.16 + energy * 0.3), 0.55);
            vec2 css = vec2(gl_FragCoord.x, size.y - gl_FragCoord.y) * scale;

            for (int i = 0; i < ${MAX_PANELS}; i++) {
                if (i >= count) break;
                vec4 panel = panels[i];
                vec2 extent = panel.zw * 0.5;
                vec2 p = css - (panel.xy + extent);
                float d = box(p, extent, 14.0);
                if (d > 22.0) continue;
                if (d > 0.0) {
                    // Just outside: the glass throws a faint coloured glow onto the page.
                    result = mix(result, mix(base, a, 0.6), (1.0 - smoothstep(0.0, 22.0, d)) * 0.10 * glass);
                    continue;
                }

                // The surface's slope, from the distance field, as a normal in screen space.
                vec2 e = vec2(1.0, 0.0);
                vec2 n = normalize(vec2(box(p + e.xy, extent, 14.0) - box(p - e.xy, extent, 14.0),
                                        box(p + e.yx, extent, 14.0) - box(p - e.yx, extent, 14.0)) + 1e-5);
                float rim = 1.0 - smoothstep(0.0, 30.0, -d);
                float bend = rim * rim * rim;

                // A thick lens: near the edge the gradient is pulled in from further out, and the
                // three colours are pulled by different amounts.
                vec2 shift = vec2(n.x, -n.y) * bend * 46.0 / (size * scale);
                float amount = 0.42 + energy * 0.3;
                vec3 glassColour = vec3(field(uv - shift * 1.00, t, amount).r,
                                        field(uv - shift * 1.15, t, amount).g,
                                        field(uv - shift * 1.30, t, amount).b);
                glassColour = mix(glassColour, mix(a, b, 0.5 + 0.5 * n.x), pow(rim, 3.0) * 0.22);

                // Caustics: thin bright lines where the lens focuses the light, strongest at the rim.
                float c = noise(p * 0.018 + vec2(t * 3.0, -t * 2.0)) * 2.0 - 1.0;
                glassColour += vec3(1.0) * pow(1.0 - abs(c), 14.0) * (0.025 + rim * 0.12);

                // The rim, lit from the pointer's side, and a soft glint under the pointer.
                vec2 toLight = normalize(pointer - (panel.xy + extent) + 1e-3);
                float lit = 0.35 + 0.65 * max(dot(n, toLight), 0.0);
                float edge = 1.0 - smoothstep(0.0, 2.2, -d);
                float glint = exp(-dot(css - pointer, css - pointer) / (2.0 * 150.0 * 150.0));
                glassColour += vec3(1.0) * (edge * lit * (0.75 + dark * 0.25) + glint * (0.10 + dark * 0.08));

                result = mix(result, glassColour, glass);
                break;
            }
            colour = vec4(result, 1.0);
        }`;

    let gl, program, canvas, frame = 0, energy = 0, target = 0, glass = 0, uniforms = {};
    let panels = [], panelsDirty = true, pointer = [window.innerWidth * 0.7, 0];
    const still = matchMedia('(prefers-reduced-motion: reduce)');
    const panelData = new Float32Array(MAX_PANELS * 4);

    const glassOn = () => Boolean(gl) && Boolean(window.poFx?.enabled());

    function retint() {
        if (!gl) return;
        // Let the swapped theme stylesheet apply before reading its colours.
        setTimeout(() => {
            // The page's own background, so the shader rests exactly on it in either theme.
            const page = getComputedStyle(document.body).backgroundColor.match(/[\d.]+/g) || [];
            const dark = document.documentElement.dataset.theme === 'dark';
            gl.uniform3fv(uniforms.base, page.length >= 3 && page[3] !== '0'
                ? page.slice(0, 3).map((v) => parseFloat(v) / 255)
                : (dark ? [0.07, 0.07, 0.09] : [0.96, 0.96, 0.97]));
            gl.uniform3fv(uniforms.a, window.poFx.colour('--po-brand-a', '#4340d2'));
            gl.uniform3fv(uniforms.b, window.poFx.colour('--po-brand-b', '#7c3aed'));
            gl.uniform1f(uniforms.dark, dark ? 1 : 0);
            draw(performance.now());
        }, 250);
    }

    // CSS pixels per canvas pixel. Glass edges want the page's own resolution; a very large
    // screen is drawn a little coarser, since all but the rims is a blur of colour anyway.
    function resize() {
        const cw = canvas.clientWidth, ch = canvas.clientHeight;
        const factor = glassOn() ? Math.max(1, Math.sqrt(cw * ch / 1.6e6)) : 2;
        const w = Math.max(1, Math.floor(cw / factor)), h = Math.max(1, Math.floor(ch / factor));
        if (canvas.width !== w || canvas.height !== h) { canvas.width = w; canvas.height = h; }
        gl.viewport(0, 0, w, h);
        gl.uniform2f(uniforms.size, w, h);
        gl.uniform1f(uniforms.scale, cw / w);
    }

    // The visible glass panels, in CSS pixels from the top left.
    function measurePanels() {
        if (panelsDirty) {
            panels = [...document.querySelectorAll('.po-glass')];
            panelsDirty = false;
        }
        let count = 0;
        for (const element of panels) {
            if (count >= MAX_PANELS) break;
            const r = element.getBoundingClientRect();
            if (r.width < 2 || r.bottom < -30 || r.top > window.innerHeight + 30) continue;
            panelData.set([r.left, r.top, r.width, r.height], count * 4);
            count++;
        }
        return count;
    }

    function draw(now) {
        energy += (target - energy) * 0.06;
        glass += ((glassOn() ? 1 : 0) - glass) * 0.08;
        resize();
        gl.uniform1f(uniforms.time, now / 1000);
        gl.uniform1f(uniforms.energy, energy);
        gl.uniform1f(uniforms.glass, glass);
        gl.uniform2f(uniforms.pointer, pointer[0], pointer[1]);
        const count = glass > 0.01 ? measurePanels() : 0;
        gl.uniform1i(uniforms.count, count);
        if (count) gl.uniform4fv(uniforms.panels, panelData);
        gl.drawArrays(gl.TRIANGLES, 0, 3);
    }

    function loop(now) {
        draw(now);
        frame = document.hidden || still.matches ? 0 : requestAnimationFrame(loop);
    }

    function start() {
        canvas = document.getElementById('po-backdrop');
        gl = canvas && canvas.getContext('webgl2', { antialias: false, powerPreference: 'low-power' });
        if (!gl) { canvas?.remove(); return; }

        try {
            program = window.poFx.program(gl, vertex, fragment);
        } catch (err) {
            console.warn('[poBackdrop]', err.message);
            canvas.remove();
            gl = null;
            return;
        }
        gl.useProgram(program);

        // One triangle that covers the screen.
        gl.bindBuffer(gl.ARRAY_BUFFER, gl.createBuffer());
        gl.bufferData(gl.ARRAY_BUFFER, new Float32Array([-1, -1, 3, -1, -1, 3]), gl.STATIC_DRAW);
        const position = gl.getAttribLocation(program, 'p');
        gl.enableVertexAttribArray(position);
        gl.vertexAttribPointer(position, 2, gl.FLOAT, false, 0, 0);
        for (const name of ['size', 'scale', 'time', 'energy', 'base', 'a', 'b', 'panels', 'count', 'pointer', 'glass', 'dark']) {
            uniforms[name] = gl.getUniformLocation(program, name);
        }

        // Panels come and go with the page; the list is re-read on the next frame after a change.
        new MutationObserver(() => { panelsDirty = true; }).observe(document.body, { childList: true, subtree: true });
        window.addEventListener('pointermove', (e) => { pointer = [e.clientX, e.clientY]; }, { passive: true });

        retint();
        refresh();
        document.addEventListener('visibilitychange', () => { if (!document.hidden && !frame) frame = requestAnimationFrame(loop); });
        still.addEventListener('change', () => { if (!still.matches && !frame) frame = requestAnimationFrame(loop); });
        frame = requestAnimationFrame(loop);
    }

    // The stylesheet makes glass panels see-through only while the shader draws their glass.
    function refresh() {
        document.documentElement.classList.toggle('po-fx-glass', glassOn());
    }

    if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', start);
    else start();

    return {
        // 0 is the resting page, 1 is "something is happening".
        pulse(level) { target = level; },
        retint,
        refresh,
    };
})();
