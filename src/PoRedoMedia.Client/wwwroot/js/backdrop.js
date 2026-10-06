// The page backdrop: one WebGL2 fragment shader drawing a slow mesh gradient in the theme's
// colours. It brightens while a file is dragged over the page and while a run is working.
// No WebGL2, or a request for reduced motion, leaves the plain page background.
window.poBackdrop = (() => {
    const vertex = `#version 300 es
        in vec2 p; void main() { gl_Position = vec4(p, 0.0, 1.0); }`;

    const fragment = `#version 300 es
        precision mediump float;
        uniform vec2 size; uniform float time; uniform float energy;
        uniform vec3 base; uniform vec3 a; uniform vec3 b;
        out vec4 colour;

        float hash(vec2 p) { return fract(sin(dot(p, vec2(127.1, 311.7))) * 43758.5453); }
        float noise(vec2 p) {
            vec2 i = floor(p), f = fract(p);
            vec2 u = f * f * (3.0 - 2.0 * f);
            return mix(mix(hash(i), hash(i + vec2(1.0, 0.0)), u.x), mix(hash(i + vec2(0.0, 1.0)), hash(i + vec2(1.0, 1.0)), u.x), u.y);
        }

        void main() {
            vec2 uv = gl_FragCoord.xy / size;
            float t = time * (0.05 + energy * 0.25);
            float n = noise(uv * 2.2 + vec2(t, -t * 0.7)) * 0.6 + noise(uv * 4.6 - vec2(t * 0.6, t)) * 0.4;
            float glow = smoothstep(0.35, 0.95, n);
            vec3 tint = mix(a, b, uv.x + 0.25 * sin(t * 3.0 + uv.y * 4.0));
            colour = vec4(mix(base, tint, glow * (0.16 + energy * 0.3)), 1.0);
        }`;

    let gl, program, canvas, frame = 0, energy = 0, target = 0, uniforms = {};
    const still = matchMedia('(prefers-reduced-motion: reduce)');

    function colour(name, fallback) {
        const probe = document.createElement('span');
        probe.style.color = getComputedStyle(document.documentElement).getPropertyValue(name).trim() || fallback;
        document.body.appendChild(probe);
        const parts = getComputedStyle(probe).color.match(/[\d.]+/g).slice(0, 3).map((v) => parseFloat(v) / 255);
        probe.remove();
        return parts;
    }

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
            gl.uniform3fv(uniforms.a, colour('--po-brand-a', '#4340d2'));
            gl.uniform3fv(uniforms.b, colour('--po-brand-b', '#7c3aed'));
            draw(performance.now());
        }, 250);
    }

    function resize() {
        // Half resolution: it is a blur of colour, and a phone should not pay for more.
        const w = Math.max(1, Math.floor(canvas.clientWidth / 2)), h = Math.max(1, Math.floor(canvas.clientHeight / 2));
        if (canvas.width !== w || canvas.height !== h) { canvas.width = w; canvas.height = h; }
        gl.viewport(0, 0, w, h);
        gl.uniform2f(uniforms.size, w, h);
    }

    function draw(now) {
        energy += (target - energy) * 0.06;
        resize();
        gl.uniform1f(uniforms.time, now / 1000);
        gl.uniform1f(uniforms.energy, energy);
        gl.drawArrays(gl.TRIANGLES, 0, 3);
    }

    function loop(now) {
        draw(now);
        frame = document.hidden || still.matches ? 0 : requestAnimationFrame(loop);
    }

    function compile(type, source) {
        const shader = gl.createShader(type);
        gl.shaderSource(shader, source);
        gl.compileShader(shader);
        return shader;
    }

    function start() {
        canvas = document.getElementById('po-backdrop');
        gl = canvas && canvas.getContext('webgl2', { antialias: false, powerPreference: 'low-power' });
        if (!gl) { canvas?.remove(); return; }

        program = gl.createProgram();
        gl.attachShader(program, compile(gl.VERTEX_SHADER, vertex));
        gl.attachShader(program, compile(gl.FRAGMENT_SHADER, fragment));
        gl.linkProgram(program);
        if (!gl.getProgramParameter(program, gl.LINK_STATUS)) { canvas.remove(); gl = null; return; }
        gl.useProgram(program);

        // One triangle that covers the screen.
        gl.bindBuffer(gl.ARRAY_BUFFER, gl.createBuffer());
        gl.bufferData(gl.ARRAY_BUFFER, new Float32Array([-1, -1, 3, -1, -1, 3]), gl.STATIC_DRAW);
        const position = gl.getAttribLocation(program, 'p');
        gl.enableVertexAttribArray(position);
        gl.vertexAttribPointer(position, 2, gl.FLOAT, false, 0, 0);
        for (const name of ['size', 'time', 'energy', 'base', 'a', 'b']) uniforms[name] = gl.getUniformLocation(program, name);

        retint();
        document.addEventListener('visibilitychange', () => { if (!document.hidden && !frame) frame = requestAnimationFrame(loop); });
        still.addEventListener('change', () => { if (!still.matches && !frame) frame = requestAnimationFrame(loop); });
        frame = requestAnimationFrame(loop);
    }

    if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', start);
    else start();

    return {
        // 0 is the resting page, 1 is "something is happening".
        pulse(level) { target = level; },
        retint,
    };
})();
