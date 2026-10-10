// Editor di testo ricco (Quill 1.3.7 da cdnjs, caricato solo quando serve).
window.eventsRichText = {
    editors: {},
    loading: null,
    load() {
        if (window.Quill) {
            return Promise.resolve();
        }
        if (!this.loading) {
            this.loading = new Promise((resolve, reject) => {
                const css = document.createElement("link");
                css.rel = "stylesheet";
                css.href = "https://cdnjs.cloudflare.com/ajax/libs/quill/1.3.7/quill.snow.min.css";
                document.head.appendChild(css);
                const script = document.createElement("script");
                script.src = "https://cdnjs.cloudflare.com/ajax/libs/quill/1.3.7/quill.min.js";
                script.onload = () => resolve();
                script.onerror = () => { this.loading = null; reject(new Error("Quill non disponibile")); };
                document.head.appendChild(script);
            });
        }
        return this.loading;
    },
    async init(id, html) {
        await this.load();
        const host = document.getElementById(id);
        if (!host) {
            return;
        }
        host.innerHTML = "";
        const surface = document.createElement("div");
        host.appendChild(surface);
        const quill = new Quill(surface, {
            theme: "snow",
            modules: {
                toolbar: [
                    [{ header: [2, 3, false] }],
                    ["bold", "italic", "underline"],
                    [{ list: "ordered" }, { list: "bullet" }],
                    ["link", "blockquote"],
                    ["clean"]
                ]
            }
        });
        if (html) {
            quill.clipboard.dangerouslyPasteHTML(html);
        }
        this.editors[id] = quill;
    },
    getHtml(id) {
        const quill = this.editors[id];
        if (!quill) {
            return "";
        }
        return quill.getText().trim().length === 0 ? "" : quill.root.innerHTML;
    }
};
