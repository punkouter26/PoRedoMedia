// The gallery's physics.
//   Shatter — a deleted card breaks into Voronoi shards (each the set of points nearer one seed
//             than any other, found by clipping the card by the half-planes between seeds) that
//             fly from the point of impact, spin, fall, bounce off the bottom of the screen and fade.
//   Fling   — a card dragged and thrown fast enough asks to be deleted; kept, it springs home.
//   Tilt    — a card leans toward the pointer in 3D, with a glint where the light would catch it.
//   Magnet  — cards picked for a bulk action lean together toward the middle of the selection.
window.poShatter = (() => {
    let dotnet = null, canvas = null, g = null, shards = [], frame = 0, last = 0;
    const handlers = {};

    // ── Shatter ───────────────────────────────────────────────────────────────────────────
    // Sutherland–Hodgman: the part of a convex polygon on the near side of a line.
    function clip(polygon, inside, cross) {
        const out = [];
        for (let i = 0; i < polygon.length; i++) {
            const a = polygon[i], b = polygon[(i + 1) % polygon.length];
            const ina = inside(a), inb = inside(b);
            if (ina) out.push(a);
            if (ina !== inb) out.push(cross(a, b));
        }
        return out;
    }

    // Voronoi cells of `seeds` inside a w × h rectangle.
    function cells(w, h, seeds) {
        return seeds.map((s, i) => {
            let cell = [[0, 0], [w, 0], [w, h], [0, h]];
            seeds.forEach((o, j) => {
                if (i === j || cell.length < 3) return;
                const mx = (s[0] + o[0]) / 2, my = (s[1] + o[1]) / 2, nx = o[0] - s[0], ny = o[1] - s[1];
                const side = (p) => (p[0] - mx) * nx + (p[1] - my) * ny;
                cell = clip(cell, (p) => side(p) <= 0, (a, b) => {
                    const t = side(a) / (side(a) - side(b));
                    return [a[0] + (b[0] - a[0]) * t, a[1] + (b[1] - a[1]) * t];
                });
            });
            return cell;
        }).filter((cell) => cell.length >= 3);
    }

    // A picture of the card: its background, thumbnail and title, to cut the shards from.
    function snapshot(card, r) {
        const density = Math.min(2, window.devicePixelRatio || 1);
        const shot = document.createElement('canvas');
        shot.width = Math.ceil(r.width * density);
        shot.height = Math.ceil(r.height * density);
        const s = shot.getContext('2d');
        s.scale(density, density);
        const style = getComputedStyle(card);
        s.fillStyle = style.backgroundColor;
        s.beginPath();
        s.roundRect(0, 0, r.width, r.height, 8);
        s.fill();
        const thumb = card.querySelector('img');
        if (thumb?.complete && thumb.naturalWidth) {
            const t = thumb.getBoundingClientRect();
            const scale = Math.max(t.width / thumb.naturalWidth, t.height / thumb.naturalHeight);
            const sw = t.width / scale, sh = t.height / scale;
            s.drawImage(thumb, (thumb.naturalWidth - sw) / 2, (thumb.naturalHeight - sh) / 2, sw, sh, t.left - r.left, t.top - r.top, t.width, t.height);
        }
        const title = card.querySelector('.gallery-title');
        if (title) {
            const t = title.getBoundingClientRect(), ts = getComputedStyle(title);
            s.fillStyle = ts.color;
            s.font = `${ts.fontWeight} ${ts.fontSize} ${ts.fontFamily}`;
            s.textBaseline = 'middle';
            s.fillText(title.textContent.trim().slice(0, 40), t.left - r.left, t.top - r.top + t.height / 2, r.width - 8);
        }
        return { shot, density };
    }

    function shatter(card) {
        const r = card.getBoundingClientRect();
        if (r.width < 4 || r.bottom < 0 || r.top > window.innerHeight) return false;
        const { shot, density } = snapshot(card, r);
        // Seeds bunch up around the point of impact, so the shards there are small.
        const impact = [r.width * (0.35 + Math.random() * 0.3), r.height * (0.35 + Math.random() * 0.3)];
        const seeds = Array.from({ length: 16 }, (_, i) => {
            const near = i < 7;
            const angle = Math.random() * Math.PI * 2, reach = near ? Math.random() * 0.25 : Math.random();
            return near
                ? [impact[0] + Math.cos(angle) * reach * r.width, impact[1] + Math.sin(angle) * reach * r.height]
                : [Math.random() * r.width, Math.random() * r.height];
        });
        for (const cell of cells(r.width, r.height, seeds)) {
            const cx = cell.reduce((s, p) => s + p[0], 0) / cell.length, cy = cell.reduce((s, p) => s + p[1], 0) / cell.length;
            const dx = cx - impact[0], dy = cy - impact[1], d = Math.hypot(dx, dy) || 1;
            const kick = 260 + Math.random() * 420;
            shards.push({
                shot, density, cell: cell.map(([x, y]) => [x - cx, y - cy]), ox: cx, oy: cy,
                x: r.left + cx, y: r.top + cy, vx: dx / d * kick, vy: dy / d * kick - 380 - Math.random() * 200,
                angle: 0, spin: (Math.random() - 0.5) * 12, age: 0, life: 1.4 + Math.random() * 0.5, bounced: false,
            });
        }
        card.style.visibility = 'hidden';
        return true;
    }

    function step(now) {
        const dt = Math.min(0.033, (now - (last || now)) / 1000) || 0.016;
        last = now;
        const floor = window.innerHeight - 6;
        window.poFx.fit(canvas, 2);
        const density = Math.min(2, window.devicePixelRatio || 1);
        g.setTransform(1, 0, 0, 1, 0, 0);
        g.clearRect(0, 0, canvas.width, canvas.height);
        g.setTransform(density, 0, 0, density, 0, 0);
        shards = shards.filter((s) => s.age < s.life);
        for (const s of shards) {
            s.age += dt;
            s.vy += 2600 * dt;
            s.x += s.vx * dt;
            s.y += s.vy * dt;
            s.angle += s.spin * dt;
            if (s.y > floor && s.vy > 0) {
                s.y = floor;
                s.vy *= -0.32;
                s.vx *= 0.7;
                s.spin *= 0.6;
            }
            const alpha = Math.min(1, (s.life - s.age) / 0.45);
            g.save();
            g.globalAlpha = alpha;
            g.translate(s.x, s.y);
            g.rotate(s.angle);
            g.beginPath();
            s.cell.forEach(([x, y], i) => (i ? g.lineTo(x, y) : g.moveTo(x, y)));
            g.closePath();
            g.save();
            g.clip();
            g.drawImage(s.shot, -s.ox, -s.oy, s.shot.width / s.density, s.shot.height / s.density);
            g.restore();
            // A bright glass edge.
            g.strokeStyle = 'rgba(255,255,255,0.7)';
            g.lineWidth = 1;
            g.stroke();
            g.restore();
        }
        frame = shards.length ? requestAnimationFrame(step) : 0;
        if (!frame) { g.setTransform(1, 0, 0, 1, 0, 0); g.clearRect(0, 0, canvas.width, canvas.height); last = 0; }
    }

    // ── Fling ─────────────────────────────────────────────────────────────────────────────
    let drag = null;

    // A damped spring back to where the card belongs.
    function springHome(card, x, y, vx, vy) {
        let previous = 0;
        const tick = (now) => {
            const dt = Math.min(0.033, (now - (previous || now)) / 1000) || 0.016;
            previous = now;
            vx += (-x * 320 - vx * 22) * dt;
            vy += (-y * 320 - vy * 22) * dt;
            x += vx * dt;
            y += vy * dt;
            card.style.translate = `${x}px ${y}px`;
            card.style.rotate = `${x * 0.04}deg`;
            if (Math.abs(x) + Math.abs(y) + Math.abs(vx) + Math.abs(vy) < 0.5) {
                card.style.translate = '';
                card.style.rotate = '';
                card.classList.remove('flinging');
                return;
            }
            requestAnimationFrame(tick);
        };
        requestAnimationFrame(tick);
    }

    function onDown(e) {
        const card = e.target.closest?.('.gallery-card');
        if (!card || e.button !== 0 || !window.poFx.enabled()) return;
        drag = { card, id: card.dataset.id, x0: e.clientX, y0: e.clientY, x: 0, y: 0, vx: 0, vy: 0, t: performance.now(), on: false, pointer: e.pointerId, touch: e.pointerType === 'touch' };
    }

    function onMove(e) {
        if (drag && e.pointerId === drag.pointer) {
            const x = e.clientX - drag.x0, y = e.clientY - drag.y0;
            if (!drag.on) {
                if (Math.hypot(x, y) < 10) return;
                // On a touch screen a mostly vertical move is a scroll, not a throw.
                if (drag.touch && Math.abs(y) > Math.abs(x)) { drag = null; return; }
                drag.on = true;
                drag.card.classList.add('flinging');
                drag.card.setPointerCapture?.(e.pointerId);
                tilt(drag.card, null);
            }
            const now = performance.now(), dt = Math.max(1, now - drag.t);
            drag.vx = drag.vx * 0.6 + (x - drag.x) / dt * 0.4;
            drag.vy = drag.vy * 0.6 + (y - drag.y) / dt * 0.4;
            Object.assign(drag, { x, y, t: now });
            drag.card.style.translate = `${x}px ${y}px`;
            drag.card.style.rotate = `${x * 0.04 + drag.vx * 3}deg`;
            return;
        }
        if (e.pointerType === 'mouse') tilt(e.target.closest?.('.gallery-card'), e);
    }

    async function onUp(e) {
        if (!drag || e.pointerId !== drag.pointer) return;
        const { card, id, x, y, vx, vy, on } = drag;
        drag = null;
        if (!on) return;
        card.dataset.dragged = '1';
        const speed = Math.hypot(vx, vy);
        if (speed > 1.1 && Math.hypot(x, y) > 80 && dotnet) {
            // Thrown: it flies on out, and the page asks whether to delete it.
            card.dataset.flung = '1';
            card.style.transition = 'translate 260ms ease-in, opacity 260ms';
            card.style.translate = `${x + vx * 260}px ${y + vy * 260}px`;
            card.style.opacity = '0';
            window.poSfx?.playAt('pop', card);
            let deleted = false;
            try { deleted = await dotnet.invokeMethodAsync('OnFling', id); } catch { /* the page is gone */ }
            if (deleted || !card.isConnected) return;
            delete card.dataset.flung;
            card.style.transition = '';
            card.style.opacity = '';
            springHome(card, x, y, 0, 0);
            return;
        }
        springHome(card, x, y, vx * 1000, vy * 1000);
    }

    // A click that ends a drag is not a click on the card.
    function onClick(e) {
        const card = e.target.closest?.('.gallery-card');
        if (card?.dataset.dragged) {
            delete card.dataset.dragged;
            e.preventDefault();
            e.stopPropagation();
        }
    }

    // ── Tilt ──────────────────────────────────────────────────────────────────────────────
    let tilted = null;

    function tilt(card, e) {
        if (tilted && tilted !== card) {
            tilted.style.removeProperty('--rx');
            tilted.style.removeProperty('--ry');
            tilted = null;
        }
        if (!card || !e || !window.poFx.enabled() || card.classList.contains('flinging')) return;
        const r = card.getBoundingClientRect();
        const nx = (e.clientX - r.left) / r.width * 2 - 1, ny = (e.clientY - r.top) / r.height * 2 - 1;
        card.style.setProperty('--rx', `${(-ny * 7).toFixed(2)}deg`);
        card.style.setProperty('--ry', `${(nx * 9).toFixed(2)}deg`);
        card.style.setProperty('--gx', `${((nx + 1) * 50).toFixed(1)}%`);
        card.style.setProperty('--gy', `${((ny + 1) * 50).toFixed(1)}%`);
        tilted = card;
    }

    return {
        /** Turns on the throw, tilt and click guard for the gallery. `self` receives OnFling(id). */
        attach(self) {
            window.poShatter.detach();
            dotnet = self;
            Object.assign(handlers, {
                pointerdown: onDown, pointermove: onMove, pointerup: onUp, pointercancel: onUp,
                dragstart: (e) => { if (e.target.closest?.('.gallery-card')) e.preventDefault(); },
                pointerleave: () => tilt(null, null),
            });
            for (const [type, handler] of Object.entries(handlers)) document.addEventListener(type, handler);
            // Captured on the window, so it runs before Blazor's own click handling.
            window.addEventListener('click', onClick, true);
        },

        detach() {
            for (const [type, handler] of Object.entries(handlers)) document.removeEventListener(type, handler);
            window.removeEventListener('click', onClick, true);
            dotnet = null;
        },

        /** Shatters the cards of these media ids. False when nothing was shown, so the caller plays its own sound. */
        burst(ids) {
            if (!window.poFx.enabled()) return false;
            canvas ??= window.poFx.layer('shatter', 1150);
            g ??= canvas.getContext('2d');
            let any = false, first = null;
            for (const id of ids) {
                const card = document.querySelector(`.gallery-card[data-id="${id}"]`);
                if (!card || card.dataset.flung) continue;
                if (shatter(card)) { any = true; first ??= card; }
            }
            if (any) {
                window.poSfx?.playAt('shatter', first);
                window.poFx.haptic([8, 30, 12]);
                if (!frame) frame = requestAnimationFrame(step);
            }
            return any;
        },

        /** The delete did not happen: the cards come back. */
        restore(ids) {
            for (const id of ids) {
                const card = document.querySelector(`.gallery-card[data-id="${id}"]`);
                if (card) card.style.visibility = '';
            }
        },

        /** The cards of these ids lean toward their common middle; all others stand straight. */
        magnet(ids) {
            const on = window.poFx.enabled();
            const picked = new Set(ids);
            const cards = [...document.querySelectorAll('.gallery-card')];
            const chosen = cards.filter((c) => picked.has(c.dataset.id));
            const centres = chosen.map((c) => { const r = c.getBoundingClientRect(); return [r.left + r.width / 2, r.top + r.height / 2]; });
            const mx = centres.reduce((s, c) => s + c[0], 0) / (centres.length || 1), my = centres.reduce((s, c) => s + c[1], 0) / (centres.length || 1);
            cards.forEach((card) => {
                const i = chosen.indexOf(card);
                if (!on || i < 0 || chosen.length < 2) {
                    card.style.removeProperty('--mx');
                    card.style.removeProperty('--my');
                    return;
                }
                const dx = mx - centres[i][0], dy = my - centres[i][1], d = Math.hypot(dx, dy) || 1;
                const pull = Math.min(9, d * 0.04);
                card.style.setProperty('--mx', `${(dx / d * pull).toFixed(1)}px`);
                card.style.setProperty('--my', `${(dy / d * pull).toFixed(1)}px`);
            });
        },
    };
})();
