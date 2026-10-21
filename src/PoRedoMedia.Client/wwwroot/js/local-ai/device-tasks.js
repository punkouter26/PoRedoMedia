// The job the user's own device can do for the server: hear the speech in a video when the server
// has no speech model. The model runs in a worker and the result goes to the server.
window.poDeviceAi = {
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
            console.warn('[poDeviceAi] On-device speech failed:', err?.message ?? err);
            return -1;
        } finally {
            worker?.terminate();
        }
    },
};
