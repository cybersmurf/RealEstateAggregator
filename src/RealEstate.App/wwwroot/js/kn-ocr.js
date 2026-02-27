/**
 * KN OCR – podpora pro paste screenshotu z katastru nemovitostí.
 * Naslouchá na Ctrl+V / paste event v rámci drop-zóny a přeposílá
 * obrázek z clipboardu do Blazor komponenty přes DotNet.invokeMethodAsync.
 */

window.knOcr = {
    _dotNetRef: null,
    _pasteHandler: null,
    _dropHandler: null,
    _dragoverHandler: null,

    /**
     * Inicializuje posluchač paste + drag&drop pro daný element.
     * dotNetRef: reference na Blazor komponentu (volá ReceivePastedImageAsync)
     * elementId: id elementu, na který se naváže paste listener (nebo document)
     */
    init: function (dotNetRef, elementId) {
        window.knOcr._dotNetRef = dotNetRef;

        // Cleanup předchozího listeneru
        window.knOcr.dispose();

        const target = elementId ? document.getElementById(elementId) : document;

        if (!target) {
            console.warn("knOcr.init: element not found:", elementId);
            return;
        }

        window.knOcr._pasteHandler = async (e) => {
            const items = (e.clipboardData || e.originalEvent?.clipboardData)?.items;
            if (!items) return;

            for (const item of items) {
                if (item.type.startsWith("image/")) {
                    e.preventDefault();
                    const file = item.getAsFile();
                    if (!file) return;

                    const base64 = await window.knOcr._fileToBase64(file);
                    await dotNetRef.invokeMethodAsync("ReceivePastedImageAsync", base64, file.type);
                    return;
                }
            }
        };

        // Drag & drop support
        window.knOcr._dragoverHandler = (e) => {
            e.preventDefault();
            e.dataTransfer.dropEffect = "copy";
            if (target !== document) target.classList.add("kn-ocr-dragover");
        };

        window.knOcr._dropHandler = async (e) => {
            e.preventDefault();
            if (target !== document) target.classList.remove("kn-ocr-dragover");

            const file = e.dataTransfer?.files?.[0];
            if (!file || !file.type.startsWith("image/")) return;

            const base64 = await window.knOcr._fileToBase64(file);
            await dotNetRef.invokeMethodAsync("ReceivePastedImageAsync", base64, file.type);
        };

        // Paste jde raději na document (funguje vždy)
        document.addEventListener("paste", window.knOcr._pasteHandler);
        target.addEventListener("dragover", window.knOcr._dragoverHandler);
        target.addEventListener("drop", window.knOcr._dropHandler);
    },

    dispose: function () {
        if (window.knOcr._pasteHandler) {
            document.removeEventListener("paste", window.knOcr._pasteHandler);
            window.knOcr._pasteHandler = null;
        }
        const el = document.getElementById("kn-ocr-dropzone");
        if (el) {
            if (window.knOcr._dragoverHandler)
                el.removeEventListener("dragover", window.knOcr._dragoverHandler);
            if (window.knOcr._dropHandler)
                el.removeEventListener("drop", window.knOcr._dropHandler);
        }
        window.knOcr._dragoverHandler = null;
        window.knOcr._dropHandler = null;
    },

    /**
     * Konvertuje File na base64 string (bez data URL prefixu).
     */
    _fileToBase64: function (file) {
        return new Promise((resolve, reject) => {
            const reader = new FileReader();
            reader.onload = () => {
                // Odstraň prefix "data:image/png;base64," → vrať čistý base64
                const result = reader.result;
                const comma = result.indexOf(",");
                resolve(comma >= 0 ? result.substring(comma + 1) : result);
            };
            reader.onerror = reject;
            reader.readAsDataURL(file);
        });
    },

    /**
     * Vrátí preview obrázku z base64 pro zobrazení v <img> tagu.
     */
    getPreviewUrl: function (base64, mimeType) {
        return `data:${mimeType};base64,${base64}`;
    }
};
