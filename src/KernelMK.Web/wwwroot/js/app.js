window.kernelMK = {
    downloadFile: function (fileName, mimeType, base64Content) {
        const link = document.createElement('a');
        link.href = `data:${mimeType};base64,${base64Content}`;
        link.download = fileName;
        document.body.appendChild(link);
        link.click();
        document.body.removeChild(link);
    }
};
