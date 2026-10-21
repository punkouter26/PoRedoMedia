// Interface sounds, synthesised with the Web Audio API so there are no audio files to ship.
// FM bells, filtered noise and a reverb whose echo is generated rather than recorded. A sound can
// be placed left or right to match where it happened on screen. Each finished step of a run plays
// the next note of a pentatonic scale, so a run climbs and resolves; while runs work, a soft pad
// plays and opens up as they progress, and it all steps back while a roast is playing.
// Muted and volume are remembered in this browser.
window.poSfx = (() => {
    let context = null, master = null, bus = null, reverb = null, noiseBuffer = null;
    let pad = null, whoosh = null, whooshStop = 0;

    const muted = () => window.poMedia.read('po.sfx.muted') === '1';
    const volume = () => {
        const stored = parseFloat(window.poMedia.read('po.sfx.volume') ?? '0.5');
        return isFinite(stored) ? Math.min(1, Math.max(0, stored)) : 0.5;
    };

    // Pentatonic C major from C5: no two notes of it clash, whatever order the steps finish in.
    const scale = [523.25, 587.33, 659.25, 783.99, 880, 1046.5, 1174.66, 1318.51, 1567.98, 1760];

    /** The output chain, made on first use: everything → duck bus (+ reverb) → volume → limiter. */
    function graph() {
        if (!context) {
            context = new AudioContext();
            const limiter = context.createDynamicsCompressor();
            limiter.threshold.value = -14;
            limiter.ratio.value = 8;
            master = context.createGain();
            master.connect(limiter).connect(context.destination);
            bus = context.createGain();
            bus.connect(master);
            reverb = context.createConvolver();
            reverb.buffer = impulse(2.2, 3);
            const wet = context.createGain();
            wet.gain.value = 0.32;
            reverb.connect(wet).connect(bus);
            noiseBuffer = context.createBuffer(1, context.sampleRate * 2, context.sampleRate);
            const data = noiseBuffer.getChannelData(0);
            for (let i = 0; i < data.length; i++) data[i] = Math.random() * 2 - 1;
        }
        if (context.state === 'suspended') context.resume();
        master.gain.setTargetAtTime(volume() * 0.55, context.currentTime, 0.02);
        return context;
    }

    // A room's echo, made of decaying noise: two channels that differ, so it sounds wide.
    function impulse(seconds, decay) {
        const length = Math.floor(context.sampleRate * seconds);
        const buffer = context.createBuffer(2, length, context.sampleRate);
        for (let c = 0; c < 2; c++) {
            const data = buffer.getChannelData(c);
            for (let i = 0; i < length; i++) data[i] = (Math.random() * 2 - 1) * Math.pow(1 - i / length, decay);
        }
        return buffer;
    }

    // Where a voice goes: placed left or right, then dry to the bus and a share into the reverb.
    function out(pan, send = 0.5) {
        const panner = context.createStereoPanner();
        panner.pan.value = Math.max(-1, Math.min(1, pan));
        panner.connect(bus);
        const share = context.createGain();
        share.gain.value = send;
        panner.connect(share).connect(reverb);
        return panner;
    }

    // A quick attack and an exponential fade, so nothing clicks.
    function envelope(gain, at, length, level, attack = 0.008) {
        gain.gain.setValueAtTime(0.0001, at);
        gain.gain.exponentialRampToValueAtTime(Math.max(0.0002, level * 0.3), at + attack);
        gain.gain.exponentialRampToValueAtTime(0.0001, at + length);
    }

    function tone(frequency, delay, length, { type = 'sine', level = 1, pan = 0, glide = 0, send = 0.4 } = {}) {
        const at = context.currentTime + delay;
        const oscillator = context.createOscillator();
        const gain = context.createGain();
        oscillator.type = type;
        oscillator.frequency.setValueAtTime(frequency, at);
        if (glide) oscillator.frequency.exponentialRampToValueAtTime(glide, at + length);
        envelope(gain, at, length, level);
        oscillator.connect(gain).connect(out(pan, send));
        oscillator.start(at);
        oscillator.stop(at + length + 0.05);
    }

    // An FM bell: a sine whose pitch is wobbled by another sine; the wobble dies away first, so
    // the strike is bright and the tail is pure.
    function bell(frequency, delay, length, { level = 1, pan = 0, ratio = 3.5, index = 2.4, send = 0.6 } = {}) {
        const at = context.currentTime + delay;
        const carrier = context.createOscillator();
        const modulator = context.createOscillator();
        const depth = context.createGain();
        const gain = context.createGain();
        carrier.frequency.value = frequency;
        modulator.frequency.value = frequency * ratio;
        depth.gain.setValueAtTime(frequency * index, at);
        depth.gain.exponentialRampToValueAtTime(frequency * 0.01, at + length * 0.6);
        modulator.connect(depth).connect(carrier.frequency);
        envelope(gain, at, length, level, 0.004);
        carrier.connect(gain).connect(out(pan, send));
        for (const o of [carrier, modulator]) { o.start(at); o.stop(at + length + 0.05); }
    }

    function noise(delay, length, { level = 1, pan = 0, filter = 'bandpass', frequency = 1200, q = 1, sweep = 0, send = 0.3 } = {}) {
        const at = context.currentTime + delay;
        const source = context.createBufferSource();
        source.buffer = noiseBuffer;
        source.loop = true;
        const shape = context.createBiquadFilter();
        shape.type = filter;
        shape.frequency.setValueAtTime(frequency, at);
        if (sweep) shape.frequency.exponentialRampToValueAtTime(sweep, at + length);
        shape.Q.value = q;
        const gain = context.createGain();
        envelope(gain, at, length, level, 0.003);
        source.connect(shape).connect(gain).connect(out(pan, send));
        source.start(at, Math.random());
        source.stop(at + length + 0.05);
    }

    // A low body blow: a sine falling fast from 120 Hz, the felt part of a drop.
    function thump(delay, level = 1, pan = 0) {
        tone(120, delay, 0.32, { level: 1.4 * level, glide: 38, pan, send: 0.15 });
        noise(delay, 0.06, { level: 0.6 * level, filter: 'lowpass', frequency: 900, pan, send: 0.1 });
    }

    const sounds = {
        start: (pan) => {
            bell(392, 0, 0.35, { pan, level: 0.9 });
            bell(587.33, 0.07, 0.5, { pan, level: 0.9 });
            noise(0, 0.35, { level: 0.25, filter: 'highpass', frequency: 600, sweep: 6000, pan });
        },
        tick: (pan) => bell(1318.51, 0, 0.12, { pan, level: 0.5, index: 1.2, send: 0.2 }),
        success: (pan) => {
            [523.25, 659.25, 783.99, 1046.5].forEach((f, i) => bell(f, i * 0.08, 0.5, { pan: pan + (i - 1.5) * 0.25, level: 0.9 }));
            [523.25, 659.25, 783.99, 1046.5].forEach((f) => bell(f, 0.34, 1.6, { pan, level: 0.45, index: 1.4 }));
            noise(0.34, 0.6, { level: 0.18, filter: 'highpass', frequency: 7000, pan, send: 0.8 });
        },
        fail: (pan) => {
            for (const detune of [0.995, 1.005]) {
                tone(311 * detune, 0, 0.2, { type: 'sawtooth', level: 0.45, pan, glide: 290 });
                tone(233 * detune, 0.16, 0.42, { type: 'sawtooth', level: 0.45, pan, glide: 196 });
            }
            noise(0, 0.5, { level: 0.2, filter: 'lowpass', frequency: 2400, sweep: 200, pan });
        },
        remove: (pan) => {
            noise(0, 0.12, { level: 0.7, frequency: 900, q: 2, pan });
            thump(0.04, 0.8, pan);
        },
        // Glass: a scatter of bright cracks and tiny inharmonic tinkles over a thump.
        shatter: (pan) => {
            for (let i = 0; i < 7; i++) noise(i * 0.025 + Math.random() * 0.02, 0.05, { level: 0.6, frequency: 2500 + Math.random() * 4000, q: 4, pan: pan + (Math.random() - 0.5) * 0.6 });
            for (let i = 0; i < 5; i++) bell(2200 + Math.random() * 2600, 0.03 + i * 0.04, 0.3, { level: 0.25, ratio: 1.41, index: 3, pan: pan + (Math.random() - 0.5) * 0.8 });
            thump(0, 0.7, pan);
        },
        drop: (pan) => { thump(0, 1, pan); bell(196, 0.02, 0.6, { level: 0.35, index: 0.8, pan }); },
        // Shimmering grains that climb, under a picture being rebuilt.
        shimmer: (pan) => {
            for (let i = 0; i < 36; i++) {
                const note = scale[Math.min(scale.length - 1, Math.floor(i / 36 * scale.length + Math.random() * 2))];
                bell(note * (Math.random() < 0.3 ? 2 : 1), i * 0.065 + Math.random() * 0.04, 0.18, { level: 0.22, index: 1.6, pan: pan + (Math.random() - 0.5) * 1.2, send: 0.8 });
            }
        },
        cue: (pan) => { thump(0, 0.6, pan); noise(0, 0.08, { level: 0.35, filter: 'highpass', frequency: 3000, pan }); },
        pop: (pan) => {
            noise(0, 0.05, { level: 0.9, filter: 'highpass', frequency: 1800, pan });
            tone(180, 0, 0.12, { level: 0.6, glide: 60, pan, send: 0.2 });
        },
        micdrop: (pan) => {
            thump(0, 1.4, pan);
            noise(0, 0.9, { level: 0.35, filter: 'lowpass', frequency: 1800, sweep: 120, pan, send: 0.9 });
            tone(82, 0.02, 1.1, { type: 'triangle', level: 0.5, glide: 41, pan });
        },
    };

    function play(name, pan = 0) {
        if (muted() || !sounds[name]) return;
        try {
            graph();
            sounds[name](pan);
        } catch { /* no audio device: stay silent */ }
    }

    // Left or right by where the element sits across the screen.
    const panOf = (element) => {
        const r = element?.getBoundingClientRect?.();
        return r ? ((r.left + r.width / 2) / window.innerWidth) * 1.6 - 0.8 : 0;
    };

    return {
        play,
        playAt(name, element) { play(name, panOf(element)); },

        /** The next note up for the n-th finished step of a run (from 0). */
        step(n, pan = 0) {
            if (muted()) return;
            try {
                graph();
                bell(scale[Math.min(n, scale.length - 1)], 0, 0.7, { level: 0.85, pan });
                bell(scale[Math.min(n, scale.length - 1)] * 2, 0.02, 0.4, { level: 0.2, index: 1, pan });
            } catch { /* silent */ }
        },

        /**
         * The working pad: two detuned saws and a sub through a low-pass that opens as the work
         * progresses. Only plays once the audio has been allowed by a click; never starts on load.
         */
        pad(on, percent = 0) {
            if (!context && !on) return;
            try {
                if (on && !muted() && !pad) {
                    graph();
                    const filter = context.createBiquadFilter();
                    filter.type = 'lowpass';
                    filter.Q.value = 5;
                    filter.frequency.value = 260;
                    const gain = context.createGain();
                    gain.gain.value = 0.0001;
                    gain.gain.setTargetAtTime(0.05, context.currentTime, 0.8);
                    const voices = [[110, 'sawtooth'], [110.6, 'sawtooth'], [55, 'sine'], [164.8, 'triangle']].map(([f, type]) => {
                        const o = context.createOscillator();
                        o.type = type;
                        o.frequency.value = f;
                        o.connect(filter);
                        o.start();
                        return o;
                    });
                    const lfo = context.createOscillator();
                    const lfoDepth = context.createGain();
                    lfo.frequency.value = 0.13;
                    lfoDepth.gain.value = 90;
                    lfo.connect(lfoDepth).connect(filter.frequency);
                    lfo.start();
                    filter.connect(gain).connect(out(0, 0.9));
                    pad = { filter, gain, voices: [...voices, lfo] };
                }
                if (!pad) return;
                if (on && !muted()) {
                    pad.filter.frequency.setTargetAtTime(260 + Math.min(100, Math.max(0, percent)) / 100 * 2400, context.currentTime, 0.6);
                } else {
                    const ending = pad;
                    pad = null;
                    ending.gain.gain.setTargetAtTime(0.0001, context.currentTime, 0.5);
                    ending.voices.forEach((o) => o.stop(context.currentTime + 3));
                }
            } catch { /* silent */ }
        },

        /** Interface sounds step back (0.25) while something else is playing, and come back after. */
        duck(on) {
            if (!context) return;
            bus.gain.setTargetAtTime(on ? 0.25 : 1, context.currentTime, 0.15);
        },

        /** Air rushing past, louder and brighter with `level` (0 to 1). Stops itself at 0. */
        whoosh(level) {
            if (muted() || (!whoosh && level <= 0)) return;
            try {
                graph();
                if (!whoosh) {
                    const source = context.createBufferSource();
                    source.buffer = noiseBuffer;
                    source.loop = true;
                    const filter = context.createBiquadFilter();
                    filter.type = 'bandpass';
                    filter.Q.value = 1.4;
                    const gain = context.createGain();
                    gain.gain.value = 0.0001;
                    source.connect(filter).connect(gain).connect(out(0, 0.5));
                    source.start();
                    whoosh = { source, filter, gain };
                }
                const now = context.currentTime;
                whoosh.gain.gain.setTargetAtTime(Math.max(0.0001, level * 0.22), now, 0.08);
                whoosh.filter.frequency.setTargetAtTime(300 + level * 2600, now, 0.08);
                clearTimeout(whooshStop);
                if (level <= 0) {
                    whooshStop = setTimeout(() => {
                        whoosh?.source.stop();
                        whoosh = null;
                    }, 600);
                }
            } catch { /* silent */ }
        },

        muted,
        volume,
        setMuted(on) {
            window.poMedia.write('po.sfx.muted', on ? '1' : '0');
            if (on) window.poSfx.pad(false);
        },
        setVolume(value) {
            window.poMedia.write('po.sfx.volume', String(value));
            if (context) master.gain.setTargetAtTime(volume() * 0.55, context.currentTime, 0.05);
        },
    };
})();
