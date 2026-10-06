// Browser actions Blazor cannot do itself. Kept small on purpose.
window.poMedia = {
    // Sends the chosen file straight from the browser to storage. The file never passes through
    // .NET memory, which matters for a 200 MB video.
    upload(input, url, progress) {
        return new Promise((resolve) => {
            const file = input.files && input.files[0];
            if (!file) { resolve(false); return; }
            const xhr = new XMLHttpRequest();
            xhr.open('PUT', url);
            xhr.setRequestHeader('x-ms-blob-type', 'BlockBlob');
            xhr.upload.onprogress = (e) => {
                if (e.lengthComputable) progress.invokeMethodAsync('Report', Math.round(e.loaded / e.total * 100));
            };
            xhr.onload = () => resolve(xhr.status >= 200 && xhr.status < 300);
            xhr.onerror = () => resolve(false);
            xhr.send(file);
        });
    },

    // How long the chosen video is, in seconds, from its header. -1 for anything that is not a
    // video this browser can read; the server decides about those.
    videoSeconds(input) {
        const file = input.files && input.files[0];
        if (!file || !file.type.startsWith('video/')) return Promise.resolve(-1);
        return new Promise((resolve) => {
            const video = document.createElement('video');
            const url = URL.createObjectURL(file);
            const done = (seconds) => { URL.revokeObjectURL(url); resolve(seconds); };
            video.preload = 'metadata';
            video.onloadedmetadata = () => done(isFinite(video.duration) ? video.duration : -1);
            video.onerror = () => done(-1);
            video.src = url;
        });
    },

    // Samples a frame every few seconds from the video file still held by the input, and posts
    // them for analysis. Done here, not in .NET: the frames are megabytes of image data that
    // would otherwise be copied into and out of the WASM heap. A frame that looks like the last
    // one kept is skipped, so a static video costs a few frames of vision instead of forty.
    // Resolves to the number of moments found, or -1 when the browser could not read the video
    // (the server then samples the frames itself).
    async analyseVideo(input, mediaId) {
        const file = input.files && input.files[0];
        if (!file) return -1;
        const video = document.createElement('video');
        video.muted = true;
        video.preload = 'auto';
        const url = URL.createObjectURL(file);
        try {
            await new Promise((resolve, reject) => {
                video.onloadedmetadata = resolve;
                video.onerror = reject;
                video.src = url;
            });
            if (!isFinite(video.duration) || video.duration <= 0) return -1;

            const every = Math.max(3, video.duration / 40);
            const scale = Math.min(1, 512 / video.videoWidth);
            const canvas = document.createElement('canvas');
            canvas.width = Math.round(video.videoWidth * scale);
            canvas.height = Math.round(video.videoHeight * scale);
            const context = canvas.getContext('2d');
            const tiny = document.createElement('canvas');
            tiny.width = tiny.height = 16;
            const tinyContext = tiny.getContext('2d', { willReadFrequently: true });
            const frames = [], timestamps = [];
            let last = null;
            for (let t = Math.min(0.5, video.duration / 2); t < video.duration; t += every) {
                await new Promise((resolve) => { video.onseeked = resolve; video.currentTime = t; });
                tinyContext.drawImage(video, 0, 0, 16, 16);
                const look = tinyContext.getImageData(0, 0, 16, 16).data;
                if (last && window.poMedia.frameDifference(look, last) < 6) continue;
                last = look;
                context.drawImage(video, 0, 0, canvas.width, canvas.height);
                frames.push(canvas.toDataURL('image/jpeg', 0.7));
                timestamps.push(t);
            }

            const response = await window.poMedia.post(`api/media/${mediaId}/frames`, { frames, timestamps });
            return response.ok ? (await response.json()).momentsFound : -1;
        } catch {
            return -1;
        } finally {
            URL.revokeObjectURL(url);
        }
    },

    // Mean brightness change between two 16x16 frames, 0 to 255.
    frameDifference(a, b) {
        let sum = 0;
        for (let i = 0; i < a.length; i += 4) {
            sum += Math.abs((a[i] + a[i + 1] + a[i + 2]) - (b[i] + b[i + 1] + b[i + 2])) / 3;
        }
        return sum / (a.length / 4);
    },

    // A JSON write with the antiforgery token, for the calls made from here rather than .NET.
    async post(path, body) {
        const token = (await (await fetch('api/antiforgery/token')).json()).token;
        return fetch(path, {
            method: 'POST',
            headers: { 'Content-Type': 'application/json', 'X-CSRF-TOKEN': token },
            body: JSON.stringify(body),
        });
    },

    // Hears what is said in a video with a speech model running in this browser, and stores it
    // with the video. Used when the server has no speech model. Resolves to the number of lines
    // heard, or -1 when it could not be done; the run then simply has no captions.
    async transcribeVideo(input, mediaId, status) {
        const file = input.files && input.files[0];
        // Decoding holds the whole soundtrack in memory, so long videos are left alone.
        if (!file || file.size > 80 * 1024 * 1024) return -1;
        let worker;
        try {
            const decoder = new AudioContext();
            const decoded = await decoder.decodeAudioData(await file.arrayBuffer());
            decoder.close();
            if (decoded.duration > 300) return -1;
            // The speech model wants one channel at 16 kHz.
            const offline = new OfflineAudioContext(1, Math.ceil(decoded.duration * 16000), 16000);
            const source = offline.createBufferSource();
            source.buffer = decoded;
            source.connect(offline.destination);
            source.start();
            const samples = (await offline.startRendering()).getChannelData(0);

            worker = new Worker('/js/local-ai/whisper-worker.js', { type: 'module' });
            const segments = await new Promise((resolve, reject) => {
                worker.onmessage = (e) => {
                    if (e.data.type === 'status') status.invokeMethodAsync('Report', e.data.percent ?? -1);
                    else if (e.data.type === 'complete') resolve(e.data.segments);
                    else reject(new Error(e.data.reason));
                };
                worker.onerror = (e) => reject(new Error(e.message));
                worker.postMessage({ samples }, [samples.buffer]);
            });
            if (!segments.length) return 0;
            const response = await window.poMedia.post(`api/media/${mediaId}/transcript`, segments);
            return response.ok ? segments.length : -1;
        } catch (err) {
            console.warn('[poMedia] On-device speech failed:', err?.message ?? err);
            return -1;
        } finally {
            worker?.terminate();
        }
    },

    // Drop, paste and the camera all hand their file to the picker's own file input, so one
    // upload path serves every way of choosing media.
    setFile(input, file) {
        const transfer = new DataTransfer();
        transfer.items.add(file);
        input.files = transfer.files;
        input.dispatchEvent(new Event('change', { bubbles: true }));
    },

    // Empties a file input, so choosing the same file again raises a change.
    clearFile(input) {
        if (input) input.value = '';
    },

    // Makes a region accept dropped files, and the page accept pasted images, for one input.
    // The listeners are remembered on the region so releaseDropAndPaste can remove them.
    acceptDropAndPaste(zone, input) {
        const over = (e) => { e.preventDefault(); zone.classList.add('dragging'); window.poBackdrop?.pulse(1); };
        const leave = () => { zone.classList.remove('dragging'); window.poBackdrop?.pulse(0); };
        const drop = (e) => {
            e.preventDefault();
            leave();
            const file = e.dataTransfer && e.dataTransfer.files[0];
            // Disabled means an upload is going. The upload reads the input's file as it goes, so
            // swapping it now would analyse the new file under the first one's name.
            if (file && !input.disabled) window.poMedia.setFile(input, file);
        };
        const paste = (e) => {
            const file = [...(e.clipboardData ? e.clipboardData.files : [])][0];
            if (file && !input.disabled && document.body.contains(input)) window.poMedia.setFile(input, file);
        };
        zone.addEventListener('dragover', over);
        zone.addEventListener('dragleave', leave);
        zone.addEventListener('drop', drop);
        document.addEventListener('paste', paste);
        zone._poIntake = { over, leave, drop, paste };
    },

    releaseDropAndPaste(zone) {
        const intake = zone && zone._poIntake;
        if (!intake) return;
        zone.removeEventListener('dragover', intake.over);
        zone.removeEventListener('dragleave', intake.leave);
        zone.removeEventListener('drop', intake.drop);
        document.removeEventListener('paste', intake.paste);
        delete zone._poIntake;
    },

    // A file another app shared to this one. The service worker kept it; this hands it to the
    // picker's input like any other chosen file. Resolves to whether there was one.
    async takeShared(input) {
        try {
            const cache = await caches.open('po-shared');
            const response = await cache.match('/shared-file');
            if (!response) return false;
            await cache.delete('/shared-file');
            const blob = await response.blob();
            const name = decodeURIComponent(response.headers.get('X-File-Name') || 'shared');
            window.poMedia.setFile(input, new File([blob], name, { type: blob.type }));
            return true;
        } catch {
            return false;
        }
    },

    hasCamera() {
        return Boolean(navigator.mediaDevices && navigator.mediaDevices.getUserMedia);
    },

    async startCamera(video) {
        try {
            video.srcObject = await navigator.mediaDevices.getUserMedia({ video: { facingMode: 'environment' }, audio: false });
            await video.play();
            return true;
        } catch {
            return false;
        }
    },

    // Longest clip the camera records, in seconds: the longest video the server takes.
    maxRecordingSeconds: 60,

    // Records the camera that is already showing, with the microphone when it is allowed: what is
    // said is part of what a roast or captions work from. Stopping hands the clip to the picker's
    // input like any chosen file. `clock` is an element that shows how long it has been going.
    async startRecording(video, input, clock) {
        if (!window.MediaRecorder || !video || !video.srcObject) return false;
        const type = ['video/webm;codecs=vp9,opus', 'video/webm;codecs=vp8,opus', 'video/webm'].find((t) => MediaRecorder.isTypeSupported(t));
        if (!type) return false;
        try {
            // The photo preview has no sound. Without a microphone the clip is simply silent.
            try {
                const withSound = await navigator.mediaDevices.getUserMedia({ video: { facingMode: 'environment' }, audio: true });
                video.srcObject.getTracks().forEach((track) => track.stop());
                video.srcObject = withSound;
                await video.play();
            } catch { /* keep the picture-only stream */ }

            const recorder = new MediaRecorder(video.srcObject, { mimeType: type, videoBitsPerSecond: 2500000 });
            const chunks = [];
            const began = Date.now();
            const show = () => {
                const seconds = Math.floor((Date.now() - began) / 1000);
                if (clock) clock.textContent = `Recording ${Math.floor(seconds / 60)}:${String(seconds % 60).padStart(2, '0')} of ${window.poMedia.maxRecordingSeconds / 60}:00`;
                if (seconds >= window.poMedia.maxRecordingSeconds && recorder.state === 'recording') recorder.stop();
            };
            recorder.ondataavailable = (e) => { if (e.data.size) chunks.push(e.data); };
            recorder.onstop = () => {
                video._poRecorder = null;
                window.poMedia.stopCamera(video);
                if (chunks.length) window.poMedia.setFile(input, new File(chunks, `camera-${Date.now()}.webm`, { type: 'video/webm' }));
            };
            video._poRecorder = recorder;
            video._poClock = setInterval(show, 250);
            show();
            recorder.start(1000);
            return true;
        } catch {
            return false;
        }
    },

    // Ends the recording and keeps it. Cancelling goes through stopCamera, which throws it away.
    stopRecording(video) {
        if (video && video._poRecorder && video._poRecorder.state === 'recording') video._poRecorder.stop();
    },

    stopCamera(video) {
        if (!video) return;
        clearInterval(video._poClock);
        if (video._poRecorder) {
            // Still set means nobody asked to keep the clip: it is dropped, not uploaded.
            video._poRecorder.onstop = null;
            if (video._poRecorder.state !== 'inactive') video._poRecorder.stop();
            video._poRecorder = null;
        }
        (video.srcObject ? video.srcObject.getTracks() : []).forEach((track) => track.stop());
        video.srcObject = null;
    },

    // Takes the current camera frame as a JPEG file and hands it to the picker's input.
    takePhoto(video, input) {
        const canvas = document.createElement('canvas');
        canvas.width = video.videoWidth;
        canvas.height = video.videoHeight;
        canvas.getContext('2d').drawImage(video, 0, 0);
        return new Promise((resolve) => canvas.toBlob((blob) => {
            window.poMedia.stopCamera(video);
            if (!blob) { resolve(false); return; }
            window.poMedia.setFile(input, new File([blob], `camera-${Date.now()}.jpg`, { type: 'image/jpeg' }));
            resolve(true);
        }, 'image/jpeg', 0.92));
    },

    download(url) {
        const a = document.createElement('a');
        a.href = url;
        // Marked as a download, so a refusal is a failed download and not a page of error text
        // in place of the app.
        a.download = '';
        a.rel = 'noopener';
        document.body.appendChild(a);
        a.click();
        a.remove();
    },

    // Several downloads, spaced out: a browser drops downloads started in the same instant.
    async downloadMany(urls) {
        for (const url of urls) {
            window.poMedia.download(url);
            await new Promise((resolve) => setTimeout(resolve, 600));
        }
    },

    copyText(text) {
        return navigator.clipboard.writeText(text).then(() => true, () => false);
    },

    // Puts a picture on the clipboard. Browsers only take PNG there, so it is redrawn as one.
    // The clipboard is handed a promise, which keeps the click's permission while the picture loads.
    copyImage(url) {
        if (!navigator.clipboard || !window.ClipboardItem) return Promise.resolve(false);
        const png = fetch(url).then((r) => r.blob()).then(createImageBitmap).then((bitmap) => {
            const canvas = document.createElement('canvas');
            canvas.width = bitmap.width;
            canvas.height = bitmap.height;
            canvas.getContext('2d').drawImage(bitmap, 0, 0);
            return new Promise((resolve, reject) => canvas.toBlob((blob) => blob ? resolve(blob) : reject(new Error('empty')), 'image/png'));
        });
        return navigator.clipboard.write([new ClipboardItem({ 'image/png': png })]).then(() => true, () => false);
    },

    // Asked when a run starts, never on page load: that is when "tell me when it is done" makes sense.
    askNotify() {
        if ('Notification' in window && Notification.permission === 'default') Notification.requestPermission().catch(() => { });
    },

    // A finished run, for someone who went to another tab: a system notification when allowed,
    // and the tab's title alternating until the tab is looked at again.
    notifyDone(title, body) {
        if (!document.hidden) return;
        if ('Notification' in window && Notification.permission === 'granted') {
            const notice = new Notification(title, { body, icon: 'icons/icon-192.png' });
            notice.onclick = () => { window.focus(); notice.close(); };
        }
        const original = document.title;
        let on = false;
        const timer = setInterval(() => { on = !on; document.title = on ? title : original; }, 1000);
        const stop = () => {
            clearInterval(timer);
            document.title = original;
            document.removeEventListener('visibilitychange', stop);
        };
        document.addEventListener('visibilitychange', stop);
    },

    // One shared player for the sound library: starting a sound stops the one before it.
    playSound(audio, url) {
        if (!audio) return;
        if (!audio.paused && audio.dataset.url === url) { audio.pause(); return; }
        audio.dataset.url = url;
        audio.src = url;
        audio.play().catch(() => { });
    },

    // Small things remembered in this browser: theme, sound, last options, recipes.
    read(key) {
        try { return localStorage.getItem(key); } catch { return null; }
    },

    write(key, value) {
        try {
            if (value === null || value === undefined) localStorage.removeItem(key);
            else localStorage.setItem(key, value);
        } catch { /* private mode: nothing is remembered */ }
    },

    theme() {
        return document.documentElement.dataset.theme || 'light';
    },

    setTheme(theme) {
        document.documentElement.dataset.theme = theme;
        const link = document.getElementById('po-theme');
        if (link) link.href = `_content/Radzen.Blazor/css/${theme === 'dark' ? 'material-dark-base' : 'material-base'}.css`;
        window.poMedia.write('po.theme', theme);
        window.poBackdrop?.retint();
    },

    // Gallery shortcuts. Keys typed into a field are left alone.
    shortcuts(target) {
        window.poMedia.releaseShortcuts();
        const handler = (e) => {
            const tag = (e.target.tagName || '').toLowerCase();
            const typing = tag === 'input' || tag === 'textarea' || tag === 'select' || e.target.isContentEditable;
            if (typing || e.ctrlKey || e.metaKey || e.altKey) return;
            if (document.querySelector('.rz-dialog')) return;
            const keys = ['/', 'ArrowLeft', 'ArrowRight', 'ArrowUp', 'ArrowDown', 'Delete', 'p', 'P', 'Enter', 'Escape'];
            if (!keys.includes(e.key)) return;
            // The grid's keys belong to the grid: on a video player, a list or a menu they are that
            // control's own. Search and Escape work from anywhere.
            const onGrid = e.target === document.body || Boolean(e.target.closest && e.target.closest('.gallery-card'));
            if (!onGrid && e.key !== '/' && e.key !== 'Escape') return;
            // Enter on a button or a link is that control's own.
            if (e.key === 'Enter' && (tag === 'button' || tag === 'a')) return;
            e.preventDefault();
            if (e.key === '/') { document.querySelector('[data-po-search] input, input[data-po-search]')?.focus(); return; }
            target.invokeMethodAsync('OnShortcut', e.key);
        };
        document.addEventListener('keydown', handler);
        window.poMedia._shortcuts = handler;
    },

    releaseShortcuts() {
        if (window.poMedia._shortcuts) document.removeEventListener('keydown', window.poMedia._shortcuts);
        window.poMedia._shortcuts = null;
    },

    scrollIntoView(selector) {
        document.querySelector(selector)?.scrollIntoView({ block: 'nearest' });
    },

    // Counts a number up to its new value instead of swapping it.
    countUp(element, value, prefix, digits) {
        if (!element) return;
        const from = parseFloat(element.dataset.value || '0');
        element.dataset.value = value;
        if (matchMedia('(prefers-reduced-motion: reduce)').matches) { element.textContent = prefix + value.toFixed(digits); return; }
        const started = performance.now();
        const step = (now) => {
            const t = Math.min(1, (now - started) / 600);
            const eased = 1 - Math.pow(1 - t, 3);
            element.textContent = prefix + (from + (value - from) * eased).toFixed(digits);
            if (t < 1) requestAnimationFrame(step);
        };
        requestAnimationFrame(step);
    },
};
