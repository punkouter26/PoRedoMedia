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

    // Samples a frame every few seconds from the video file still held by the input, and posts
    // them for analysis. Done here, not in .NET: the frames are megabytes of image data that
    // would otherwise be copied into and out of the WASM heap. Resolves to the number of moments
    // found, or -1 when the browser could not read the video (the run then places by time).
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
            const frames = [], timestamps = [];
            for (let t = Math.min(0.5, video.duration / 2); t < video.duration; t += every) {
                await new Promise((resolve) => { video.onseeked = resolve; video.currentTime = t; });
                context.drawImage(video, 0, 0, canvas.width, canvas.height);
                frames.push(canvas.toDataURL('image/jpeg', 0.7));
                timestamps.push(t);
            }

            const token = (await (await fetch('api/antiforgery/token')).json()).token;
            const response = await fetch(`api/media/${mediaId}/frames`, {
                method: 'POST',
                headers: { 'Content-Type': 'application/json', 'X-CSRF-TOKEN': token },
                body: JSON.stringify({ frames, timestamps }),
            });
            return response.ok ? (await response.json()).momentsFound : -1;
        } catch {
            return -1;
        } finally {
            URL.revokeObjectURL(url);
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

    // Makes a region accept dropped files, and the page accept pasted images, for one input.
    acceptDropAndPaste(zone, input) {
        const stop = (e) => { e.preventDefault(); };
        zone.addEventListener('dragover', stop);
        zone.addEventListener('drop', (e) => {
            e.preventDefault();
            const file = e.dataTransfer && e.dataTransfer.files[0];
            if (file) window.poMedia.setFile(input, file);
        });
        const onPaste = (e) => {
            const file = [...(e.clipboardData ? e.clipboardData.files : [])][0];
            if (file && document.body.contains(input)) window.poMedia.setFile(input, file);
        };
        document.addEventListener('paste', onPaste);
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

    stopCamera(video) {
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
        a.rel = 'noopener';
        document.body.appendChild(a);
        a.click();
        a.remove();
    },

    copyText(text) {
        return navigator.clipboard.writeText(text).then(() => true, () => false);
    },
};
