window.markdownViewer = (function () {
    function setHighlightTheme(href) {
        const link = document.getElementById("highlight-theme");
        if (!link) return;
        link.href = href;
    }

    function setPageTheme(href) {
        const link = document.getElementById("page-theme");
        if (!link) return;
        link.href = href;
    }

    function setContent(html) {
        const root = document.getElementById("markdown-root");
        if (!root) return;

        root.innerHTML = html || "";

        applyHighlighting(root);
        addCopyButtons(root);
    }

    function applyHighlighting(root) {
        if (!window.hljs || !root) return;

        root.querySelectorAll("pre > code:not([data-highlighted])").forEach(function (codeBlock) {
            hljs.highlightElement(codeBlock);
        });
    }

    function addCopyButtons(root) {
        if (!root) return;

        const codeBlocks = root.querySelectorAll("pre > code");

        codeBlocks.forEach(function (codeBlock) {
            const pre = codeBlock.parentElement;
            if (!pre) return;

            if (pre.parentElement && pre.parentElement.classList.contains("code-block-wrapper")) {
                return;
            }

            const wrapper = document.createElement("div");
            wrapper.className = "code-block-wrapper";

            pre.parentNode.insertBefore(wrapper, pre);
            wrapper.appendChild(pre);

            const button = document.createElement("button");
            button.className = "code-copy-button";
            button.type = "button";
            button.textContent = "Copy";

            button.addEventListener("click", function () {
                const codeText = codeBlock.textContent || "";

                if (window.chrome && window.chrome.webview) {
                    window.chrome.webview.postMessage("copy-code:" + codeText);
                }

                button.textContent = "Copied";
                button.classList.add("copied");

                setTimeout(function () {
                    button.textContent = "Copy";
                    button.classList.remove("copied");
                }, 1200);
            });

            wrapper.appendChild(button);
        });
    }

    function refreshHighlighting() {
        const root = document.getElementById("markdown-root");
        if (!root) return;

        applyHighlighting(root);
    }

    return {
        setContent: setContent,
        setPageTheme: setPageTheme,
        setHighlightTheme: setHighlightTheme,
        refreshHighlighting: refreshHighlighting
    };
})();