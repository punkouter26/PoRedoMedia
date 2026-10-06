// The roast stage: lights up the lyric line being performed and pulses with the track's
// loudness. The lyrics carry no timings, so each line gets a share of the track in proportion to
// its length. Loudness is measured from a decoded copy of the track; if that copy cannot be
// fetched the lines still light up, only without the pulse.
window.poRoast = {
    async attach(stage, audio) {
        if (!stage || !audio) return;
        window.poRoast.detach(stage);
        const lines = [...stage.querySelectorAll('[data-line]')];
        const weights = lines.map((line) => Math.max(8, line.textContent.length));
        const total = weights.reduce((sum, w) => sum + w, 0) || 1;
        // Where each line ends, as a fraction of the track.
        const ends = [];
        weights.reduce((sum, w) => { ends.push((sum + w) / total); return sum + w; }, 0);

        const state = { raf: 0, envelope: null, rate: 50 };
        stage._poRoast = state;

        const show = () => {
            const length = audio.duration || 0;
            if (!length) return;
            // A short lead-in and tail: tracks open and close on the beat alone.
            const lead = Math.min(4, length * 0.08), tail = Math.min(3, length * 0.05);
            const position = Math.min(1, Math.max(0, (audio.currentTime - lead) / Math.max(1, length - lead - tail)));
            const current = audio.currentTime < lead ? -1 : ends.findIndex((end) => position <= end);
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

        if (matchMedia('(prefers-reduced-motion: reduce)').matches) return;
        try {
            const bytes = await (await fetch(audio.currentSrc || audio.src)).arrayBuffer();
            const context = new AudioContext();
            const decoded = await context.decodeAudioData(bytes);
            context.close();
            const samples = decoded.getChannelData(0);
            const hop = Math.floor(decoded.sampleRate / state.rate);
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
            const url = URL.createObjectURL(blob);
            const a = document.createElement('a');
            a.href = url;
            a.download = 'roast-card.png';
            document.body.appendChild(a);
            a.click();
            a.remove();
            setTimeout(() => URL.revokeObjectURL(url), 5000);
            return true;
        } catch {
            return false;
        }
    },
};
