// Fill-in window for %(Input)/%(Fill) macros. Talks to FillWindow.cs with plain { type } messages.
(() => {
    const host = window.chrome?.webview;
    const send = (message) => host?.postMessage(JSON.stringify(message));
    const $ = (id) => document.getElementById(id);
    const escapeHtml = (text) => String(text).replace(/[&<>"']/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
    let fields = [];

    host?.addEventListener('message', (event) => {
        const message = JSON.parse(event.data);
        if (message.type !== 'init') return;
        document.documentElement.dataset.theme = message.theme || 'freevy';
        fields = message.fields;
        $('fields').innerHTML = fields.map((f, i) => `
            <label class="field"><span>${escapeHtml(f.label)}</span>
                <input class="input" data-index="${i}" autocomplete="off" spellcheck="false">
            </label>`).join('');
        document.querySelector('#fields input')?.focus();
    });

    $('form').addEventListener('submit', (event) => {
        event.preventDefault();
        const values = {};
        document.querySelectorAll('#fields input').forEach((input) => { values[fields[input.dataset.index].key] = input.value; });
        send({ type: 'submit', values });
    });
    $('cancel').addEventListener('click', () => send({ type: 'cancel' }));
    $('cancel-x').addEventListener('click', () => send({ type: 'cancel' }));
    document.addEventListener('keydown', (event) => {
        if (event.key === 'Escape') send({ type: 'cancel' });
    });
    $('bar').addEventListener('mousedown', (event) => {
        if (event.button === 0 && !event.target.closest('button')) send({ type: 'drag' });
    });

    send({ type: 'ready' });
})();
