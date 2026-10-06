// The roast stage: lights up the lyric line being performed and pulses with the track's
// loudness. The lyrics carry no timings, so each line gets a share of the track in proportion to
// its length. Loudness is measured from a decoded copy of the track; if that copy cannot be
// fetched the lines still light up, only without the pulse.
window.poRoast = {
    // Where each line ends, as a fraction of the sung part of the track.
    lineEnds(texts) {
        const weights = texts.map((text) => Math.max(8, text.length));
        const total = weights.reduce((sum, w) => sum + w, 0) || 1;
        const ends = [];
        weights.reduce((sum, w) => { ends.push((sum + w) / total); return sum + w; }, 0);
        return ends;
    },

    // A short lead-in and tail: tracks open and close on the beat alone.
    span(length) {
        const lead = Math.min(4, length * 0.08), tail = Math.min(3, length * 0.05);
        return { lead, sung: Math.max(1, length - lead - tail) };
    },

    // Loudness of a decoded track, `rate` values a second, scaled so the loudest is 1.
    envelope(decoded, rate) {
        const samples = decoded.getChannelData(0);
        const hop = Math.floor(decoded.sampleRate / rate);
        const envelope = new Float32Array(Math.ceil(samples.length / hop));
        let peak = 0.0001;
        for (let i = 0; i < envelope.length; i++) {
            let sum = 0;
            const end = Math.min(samples.length, (i + 1) * hop);
            for (let j = i * hop; j < end; j++) sum += samples[j] * samples[j];
            envelope[i] = Math.sqrt(sum / hop);
            peak = Math.max(peak, envelope[i]);
        }
        for (let i = 0; i < envelope.length; i++) envelope[i] /= peak;
        return envelope;
    },

    async attach(stage, audio) {
        if (!stage || !audio) return;
        window.poRoast.detach(stage);
        const lines = [...stage.querySelectorAll('[data-line]')];
        const ends = window.poRoast.lineEnds(lines.map((line) => line.textContent));
        const sync = stage.querySelector('[data-sync]');
        const syncOut = stage.querySelector('[data-sync-out]');

        const state = { raf: 0, envelope: null, rate: 50, offset: 0 };
        stage._poRoast = state;

        const show = () => {
            const length = audio.duration || 0;
            if (!length) return;
            const { lead, sung } = window.poRoast.span(length);
            // The line timings are an estimate; the sync slider moves them all earlier or later.
            const at = audio.currentTime - state.offset;
            const position = Math.min(1, Math.max(0, (at - lead) / sung));
            const current = at < lead ? -1 : ends.findIndex((end) => position <= end);
            lines.forEach((line, i) => {
                line.classList.toggle('now', i === current);
                line.classList.toggle('sung', current >= 0 && i < current);
            });
            if (current >= 0) lines[current].scrollIntoView({ block: 'nearest', behavior: 'smooth' });
            if (state.envelope) {
                const level = state.envelope[Math.min(state.envelope.length - 1, Math.floor(audio.currentTime * state.rate))] || 0;
                stage.style.setProperty('--beat', level.toFixed(3));
            }
        };

        const loop = () => { show(); state.raf = audio.paused ? 0 : requestAnimationFrame(loop); };
        state.play = () => { if (!state.raf) state.raf = requestAnimationFrame(loop); };
        state.stop = () => { show(); stage.style.setProperty('--beat', '0'); };
        audio.addEventListener('play', state.play);
        audio.addEventListener('pause', state.stop);
        audio.addEventListener('seeked', show);
        state.audio = audio;

        // A line is a button: it starts the track from where that line begins.
        lines.forEach((line, i) => line.addEventListener('click', () => {
            const length = audio.duration || 0;
            if (!length) return;
            const { lead, sung } = window.poRoast.span(length);
            audio.currentTime = Math.max(0, lead + (i === 0 ? 0 : ends[i - 1]) * sung + state.offset);
            audio.play().catch(() => { });
        }));

        if (sync) {
            sync.addEventListener('input', () => {
                state.offset = parseFloat(sync.value) || 0;
                if (syncOut) syncOut.textContent = state.offset === 0 ? 'on the beat' : `${state.offset > 0 ? '+' : ''}${state.offset.toFixed(1)}s`;
                show();
            });
        }

        if (matchMedia('(prefers-reduced-motion: reduce)').matches) return;
        try {
            const bytes = await (await fetch(audio.currentSrc || audio.src)).arrayBuffer();
            const context = new AudioContext();
            const decoded = await context.decodeAudioData(bytes);
            context.close();
            const envelope = window.poRoast.envelope(decoded, state.rate);
            if (stage._poRoast === state) state.envelope = envelope;
        } catch { /* the lines still follow the track */ }
    },

    detach(stage) {
        const state = stage && stage._poRoast;
        if (!state) return;
        cancelAnimationFrame(state.raf);
        state.audio.removeEventListener('play', state.play);
        state.audio.removeEventListener('pause', state.stop);
        delete stage._poRoast;
    },

    save(blob, name) {
        const url = URL.createObjectURL(blob);
        const a = document.createElement('a');
        a.href = url;
        a.download = name;
        document.body.appendChild(a);
        a.click();
        a.remove();
        setTimeout(() => URL.revokeObjectURL(url), 5000);
    },

    // Draws the picture with the roast under it and downloads the card as a PNG.
    async card(imageUrl, title, lyrics) {
        try {
            const bitmap = await createImageBitmap(await (await fetch(imageUrl)).blob());
            const width = 1080, pad = 56;
            const picture = Math.round(width * bitmap.height / bitmap.width);
            const text = lyrics.split('\n').map((l) => l.trim()).filter(Boolean).slice(0, 14);
            const canvas = document.createElement('canvas');
            canvas.width = width;
            canvas.height = picture + pad * 2 + 64 + text.length * 46;
            const g = canvas.getContext('2d');
            g.fillStyle = '#16181d';
            g.fillRect(0, 0, canvas.width, canvas.height);
            g.drawImage(bitmap, 0, 0, width, picture);
            g.fillStyle = '#ffffff';
            g.font = '800 44px system-ui, sans-serif';
            g.fillText(title.slice(0, 40), pad, picture + pad + 30);
            g.font = '500 30px system-ui, sans-serif';
            g.fillStyle = '#d7d9e0';
            text.forEach((line, i) => g.fillText(line.slice(0, 64), pad, picture + pad + 90 + i * 46));
            const blob = await new Promise((resolve) => canvas.toBlob(resolve, 'image/png'));
            window.poRoast.save(blob, 'roast-card.png');
            return true;
        } catch {
            return false;
        }
    },

    // Records the roast as a video: the picture, the line being delivered, and the track. It is
    // recorded as it plays, so it takes as long as the track. The meme cut zooms and shakes with
    // the beat and stamps the ending; the classic cut is the picture and clean type.
    // Resolves to false when this browser cannot record, or something it needs could not be read.
    async video(imageUrl, audioUrl, lyrics, meme, progress) {
        if (!window.MediaRecorder || !HTMLCanvasElement.prototype.captureStream) return false;
        let context;
        try {
            const bitmap = await createImageBitmap(await (await fetch(imageUrl)).blob());
            context = new AudioContext();
            await context.resume();
            const decoded = await context.decodeAudioData(await (await fetch(audioUrl)).arrayBuffer());
            const rate = 50;
            const envelope = window.poRoast.envelope(decoded, rate);
            const texts = lyrics.split('\n').map((l) => l.trim()).filter(Boolean);
            const ends = window.poRoast.lineEnds(texts);
            const { lead, sung } = window.poRoast.span(decoded.duration);

            const size = 720;
            const canvas = document.createElement('canvas');
            canvas.width = canvas.height = size;
            const g = canvas.getContext('2d');
            const cover = Math.max(size / bitmap.width, size / bitmap.height);

            // Breaks a line into rows that fit the frame.
            const rows = (text) => {
                const out = [];
                let row = '';
                for (const word of text.split(/\s+/)) {
                    const next = row ? `${row} ${word}` : word;
                    if (row && g.measureText(next).width > size - 80) { out.push(row); row = word; } else row = next;
                }
                if (row) out.push(row);
                return out.slice(0, 4);
            };

            const draw = (t) => {
                const level = envelope[Math.min(envelope.length - 1, Math.floor(t * rate))] || 0;
                const zoom = cover * (meme ? 1.04 + level * 0.14 : 1);
                const shake = meme ? level * 12 : 0;
                g.fillStyle = '#000';
                g.fillRect(0, 0, size, size);
                g.drawImage(
                    bitmap,
                    (size - bitmap.width * zoom) / 2 + (Math.random() - 0.5) * shake,
                    (size - bitmap.height * zoom) / 2 + (Math.random() - 0.5) * shake,
                    bitmap.width * zoom, bitmap.height * zoom);

                if (meme) {
                    const vignette = g.createRadialGradient(size / 2, size / 2, size * 0.3, size / 2, size / 2, size * 0.75);
                    vignette.addColorStop(0, 'rgba(200,0,0,0)');
                    vignette.addColorStop(1, `rgba(200,0,0,${(level * 0.45).toFixed(3)})`);
                    g.fillStyle = vignette;
                    g.fillRect(0, 0, size, size);
                }

                const shade = g.createLinearGradient(0, size * 0.5, 0, size);
                shade.addColorStop(0, 'rgba(0,0,0,0)');
                shade.addColorStop(1, 'rgba(0,0,0,0.85)');
                g.fillStyle = shade;
                g.fillRect(0, size * 0.5, size, size * 0.5);

                const position = Math.min(1, Math.max(0, (t - lead) / sung));
                const current = t < lead ? -1 : ends.findIndex((end) => position <= end);
                if (current >= 0) {
                    g.font = meme ? '900 44px Impact, system-ui, sans-serif' : '700 36px system-ui, sans-serif';
                    g.textAlign = 'center';
                    g.lineJoin = 'round';
                    const text = rows(meme ? texts[current].toUpperCase() : texts[current]);
                    text.forEach((row, i) => {
                        const y = size - 60 - (text.length - 1 - i) * 52;
                        if (meme) { g.lineWidth = 8; g.strokeStyle = '#000'; g.strokeText(row, size / 2, y); }
                        g.fillStyle = '#fff';
                        g.fillText(row, size / 2, y);
                    });
                }

                if (meme && t > decoded.duration - 1.5) {
                    g.save();
                    g.translate(size / 2, size * 0.32);
                    g.rotate(-0.16);
                    g.font = '900 96px Impact, system-ui, sans-serif';
                    g.textAlign = 'center';
                    g.lineWidth = 10;
                    g.strokeStyle = '#fff';
                    g.strokeText('ROASTED!', 0, 0);
                    g.fillStyle = '#d00000';
                    g.fillText('ROASTED!', 0, 0);
                    g.restore();
                } else if (!meme) {
                    g.fillStyle = '#fff';
                    g.fillRect(0, size - 6, size * Math.min(1, t / decoded.duration), 6);
                }
            };

            const sound = context.createMediaStreamDestination();
            const source = context.createBufferSource();
            source.buffer = decoded;
            source.connect(sound);
            const stream = new MediaStream([...canvas.captureStream(30).getVideoTracks(), ...sound.stream.getAudioTracks()]);
            const type = ['video/webm;codecs=vp9,opus', 'video/webm', 'video/mp4'].find((t) => MediaRecorder.isTypeSupported(t));
            if (!type) return false;
            const recorder = new MediaRecorder(stream, { mimeType: type });
            const chunks = [];
            recorder.ondataavailable = (e) => { if (e.data.size) chunks.push(e.data); };
            const stopped = new Promise((resolve) => { recorder.onstop = resolve; });

            draw(0);
            recorder.start();
            const began = context.currentTime;
            source.start();
            let reported = -1;
            await new Promise((resolve) => {
                const frame = () => {
                    const t = context.currentTime - began;
                    if (t >= decoded.duration) { resolve(); return; }
                    draw(t);
                    const percent = Math.floor(100 * t / decoded.duration);
                    if (percent !== reported) { reported = percent; progress?.invokeMethodAsync('Report', percent); }
                    requestAnimationFrame(frame);
                };
                requestAnimationFrame(frame);
            });
            recorder.stop();
            await stopped;
            window.poRoast.save(new Blob(chunks, { type }), `roast.${type.startsWith('video/mp4') ? 'mp4' : 'webm'}`);
            return true;
        } catch {
            return false;
        } finally {
            context?.close();
        }
    },
};
