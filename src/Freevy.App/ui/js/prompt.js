// Master password prompt window. Talks to PromptWindow.cs with plain { type } messages.
(() => {
    const host = window.chrome?.webview;
    const send = (message) => host?.postMessage(JSON.stringify(message));
    const $ = (id) => document.getElementById(id);

    host?.addEventListener('message', (event) => {
        const message = JSON.parse(event.data);
        if (message.type === 'init') {
            $('reason').textContent = message.reason;
            $('minutes').value = message.minutes;
            document.documentElement.dataset.theme = message.theme || 'freevy';
            $('password').focus();
        } else if (message.type === 'error') {
            $('error').textContent = message.message;
            $('error').hidden = false;
            $('unlock').disabled = false;
            $('unlock').classList.remove('is-busy');
            $('password').select();
        }
    });

    $('form').addEventListener('submit', (event) => {
        event.preventDefault();
        $('error').hidden = true;
        $('unlock').disabled = true;
        $('unlock').classList.add('is-busy');
        send({ type: 'unlock', password: $('password').value, minutes: Number($('minutes').value) || 5 });
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
