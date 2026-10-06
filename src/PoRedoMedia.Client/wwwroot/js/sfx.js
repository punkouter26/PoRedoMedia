// Interface sounds, synthesised with the Web Audio API so there are no audio files to ship.
// Muted and volume are remembered in this browser.
window.poSfx = (() => {
    let context = null;

    const muted = () => window.poMedia.read('po.sfx.muted') === '1';
    const volume = () => {
        const stored = parseFloat(window.poMedia.read('po.sfx.volume') ?? '0.5');
        return isFinite(stored) ? Math.min(1, Math.max(0, stored)) : 0.5;
    };

    // One note: a quick attack and an exponential fade, so nothing clicks.
    function note(frequency, at, length, type, level) {
        const oscillator = context.createOscillator();
        const gain = context.createGain();
        oscillator.type = type;
        oscillator.frequency.setValueAtTime(frequency, context.currentTime + at);
        gain.gain.setValueAtTime(0.0001, context.currentTime + at);
        gain.gain.exponentialRampToValueAtTime(Math.max(0.0002, level * volume() * 0.25), context.currentTime + at + 0.012);
        gain.gain.exponentialRampToValueAtTime(0.0001, context.currentTime + at + length);
        oscillator.connect(gain).connect(context.destination);
        oscillator.start(context.currentTime + at);
        oscillator.stop(context.currentTime + at + length + 0.02);
    }

    const sounds = {
        start: () => { note(392, 0, 0.09, 'triangle', 1); note(587, 0.08, 0.14, 'triangle', 1); },
        tick: () => { note(880, 0, 0.05, 'sine', 0.6); },
        success: () => { [523, 659, 784, 1047].forEach((f, i) => note(f, i * 0.085, 0.22, 'triangle', 1)); },
        fail: () => { note(311, 0, 0.18, 'sawtooth', 0.7); note(233, 0.16, 0.3, 'sawtooth', 0.7); },
        remove: () => { note(196, 0, 0.12, 'square', 0.5); note(131, 0.06, 0.16, 'sine', 0.9); },
    };

    return {
        play(name) {
            if (muted() || !sounds[name]) return;
            try {
                context ??= new AudioContext();
                if (context.state === 'suspended') context.resume();
                sounds[name]();
            } catch { /* no audio device: stay silent */ }
        },
        muted,
        volume,
        setMuted(on) { window.poMedia.write('po.sfx.muted', on ? '1' : '0'); },
        setVolume(value) { window.poMedia.write('po.sfx.volume', String(value)); },
    };
})();
