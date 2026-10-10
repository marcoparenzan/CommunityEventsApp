// Editor del testo CSV (Monaco, caricato da cdnjs solo quando serve) con una tinta diversa per ogni colonna.
window.eventsCsvEditor = {
    editors: {},
    loading: null,
    base: "https://cdnjs.cloudflare.com/ajax/libs/monaco-editor/0.52.2/min",

    load() {
        if (window.monaco) {
            return Promise.resolve();
        }
        if (!this.loading) {
            const base = this.base;
            this.loading = new Promise((resolve, reject) => {
                // I worker di Monaco devono stare sullo stesso "origin": si passa da un data URL che importa lo script dalla CDN.
                window.MonacoEnvironment = {
                    getWorkerUrl: () => "data:text/javascript;charset=utf-8," + encodeURIComponent(
                        `self.MonacoEnvironment = { baseUrl: '${base}/' }; importScripts('${base}/vs/base/worker/workerMain.js');`)
                };
                const script = document.createElement("script");
                script.src = base + "/vs/loader.min.js";
                script.onload = () => {
                    window.require.config({ paths: { vs: base + "/vs" } });
                    window.require(["vs/editor/editor.main"], () => resolve(), reject);
                };
                script.onerror = () => { this.loading = null; reject(new Error("Monaco non disponibile")); };
                document.head.appendChild(script);
            });
        }
        return this.loading;
    },

    async init(id, text, placeholder) {
        await this.load();
        const host = document.getElementById(id);
        if (!host) {
            return;
        }
        this.dispose(id);
        host.innerHTML = "";
        const surface = document.createElement("div");
        surface.className = "csv-editor-surface";
        const hint = document.createElement("div");
        hint.className = "csv-editor-placeholder";
        hint.textContent = placeholder || "";
        host.appendChild(surface);
        host.appendChild(hint);

        const editor = monaco.editor.create(surface, {
            value: text || "",
            language: "plaintext",
            theme: "vs",
            automaticLayout: true,
            minimap: { enabled: false },
            scrollBeyondLastLine: false,
            wordWrap: "off",
            fontSize: 13,
            lineNumbers: "on",
            renderWhitespace: "none",
            unicodeHighlight: { ambiguousCharacters: false, invisibleCharacters: false },
            ariaLabel: "Testo CSV"
        });
        editor.getModel().setEOL(monaco.editor.EndOfLineSequence.LF);
        const collection = editor.createDecorationsCollection([]);
        const refresh = () => {
            hint.style.display = editor.getModel().getValueLength() === 0 ? "block" : "none";
            collection.set(this.decorations(editor.getModel().getValue()));
        };
        let timer = 0;
        editor.onDidChangeModelContent(() => {
            window.clearTimeout(timer);
            timer = window.setTimeout(refresh, 120);
        });
        refresh();
        this.editors[id] = editor;
    },

    // Una classe per colonna (csv-col-0 … csv-col-7, poi si ripete); la prima riga (intestazioni) in grassetto.
    decorations(text) {
        const firstLine = text.split("\n", 1)[0];
        const delimiter = [";", ",", "\t"]
            .map(candidate => [candidate, firstLine.split(candidate).length])
            .sort((a, b) => b[1] - a[1])[0][0];
        const lines = text.split("\n");
        const result = [];
        let column = 0;
        let inQuotes = false;
        for (let i = 0; i < lines.length && result.length < 200000; i++) {
            let line = lines[i];
            if (line.endsWith("\r")) {
                line = line.slice(0, -1);
            }
            if (!inQuotes) {
                column = 0;
            }
            let start = 0;
            const push = (from, to) => {
                if (to > from) {
                    result.push({
                        range: new monaco.Range(i + 1, from + 1, i + 1, to + 1),
                        options: { inlineClassName: "csv-col-" + (column % 8) + (i === 0 ? " csv-header" : "") }
                    });
                }
            };
            for (let j = 0; j < line.length; j++) {
                const ch = line[j];
                if (ch === '"') {
                    inQuotes = !inQuotes;
                } else if (ch === delimiter && !inQuotes) {
                    push(start, j);
                    result.push({
                        range: new monaco.Range(i + 1, j + 1, i + 1, j + 2),
                        options: { inlineClassName: "csv-delimiter" }
                    });
                    column++;
                    start = j + 1;
                }
            }
            push(start, line.length);
        }
        return result;
    },

    setText(id, text) {
        const editor = this.editors[id];
        if (editor) {
            editor.setValue(text || "");
            editor.setScrollPosition({ scrollTop: 0, scrollLeft: 0 });
        }
    },

    getText(id) {
        const editor = this.editors[id];
        return editor ? editor.getModel().getValue() : "";
    },

    dispose(id) {
        const editor = this.editors[id];
        if (editor) {
            editor.dispose();
            delete this.editors[id];
        }
    }
};
