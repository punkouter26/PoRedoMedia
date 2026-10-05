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
