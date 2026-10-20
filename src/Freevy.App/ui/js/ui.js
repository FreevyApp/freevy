// Reusable UI pieces: escaping, icons, dialogs, popover menus and toasts.

export const $ = (selector, root = document) => root.querySelector(selector);
export const $$ = (selector, root = document) => [...root.querySelectorAll(selector)];
export const number = new Intl.NumberFormat('en');

export function escapeHtml(text) {
    return String(text ?? '').replace(/[&<>"']/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
}

export const icon = (name) => `<svg class="icon" aria-hidden="true"><use href="#i-${name}"/></svg>`;

export function timeAgo(iso) {
    if (!iso) {
        return 'never';
    }

    const date = new Date(iso);
    const minutes = Math.round((Date.now() - date.getTime()) / 60000);
    if (minutes < 1) return 'just now';
    if (minutes < 60) return `${minutes} min ago`;
    const sameDay = date.toDateString() === new Date().toDateString();
    return sameDay ? date.toTimeString().slice(0, 5) : date.toLocaleDateString(undefined, { day: 'numeric', month: 'short' }) + ' ' + date.toTimeString().slice(0, 5);
}

// ---- Dialog ---------------------------------------------------------------------

/**
 * Opens a native <dialog>. `fields` render as inputs; `onSubmit(values)` may throw to show
 * an error and keep the dialog open. Resolves with the values, or null when cancelled.
 */
export function dialog({ title, body = '', fields = [], checks = [], confirm = 'OK', cancel = 'Cancel', danger = false, onSubmit }) {
    return new Promise((resolve) => {
        const element = document.createElement('dialog');
        element.className = 'dialog';
        element.innerHTML = `
            <form method="dialog">
                <h2>${escapeHtml(title)}</h2>
                ${body ? `<p>${body}</p>` : ''}
                ${fields.map((f, i) => `
                    <label class="field"><span>${escapeHtml(f.label)}</span>
                        <input class="input" name="f${i}" type="${f.type ?? 'text'}" value="${escapeHtml(f.value ?? '')}" ${f.placeholder ? `placeholder="${escapeHtml(f.placeholder)}"` : ''} ${f.required === false ? '' : 'required'} autocomplete="${f.autocomplete ?? 'off'}" spellcheck="false">
                    </label>`).join('')}
                ${checks.map((c, i) => `<label class="check"><input type="checkbox" name="c${i}" ${c.checked ? 'checked' : ''}><span>${escapeHtml(c.label)}</span></label>`).join('')}
                <div class="error" hidden></div>
                <div class="dialog-actions">
                    ${cancel ? `<button type="button" class="btn btn--ghost" value="cancel">${escapeHtml(cancel)}</button>` : ''}
                    <button type="submit" class="btn ${danger ? 'btn--danger-solid' : 'btn--primary'}">${escapeHtml(confirm)}</button>
                </div>
            </form>`;
        document.body.append(element);

        const form = $('form', element);
        const error = $('.error', element);
        const submit = $('[type="submit"]', element);
        let done = false;
        const finish = (value) => {
            if (done) return;
            done = true;
            element.close();
            // Remove after the exit transition.
            setTimeout(() => element.remove(), 300);
            resolve(value);
        };

        $('[value="cancel"]', element)?.addEventListener('click', () => finish(null));
        element.addEventListener('cancel', (event) => { event.preventDefault(); finish(null); });
        form.addEventListener('submit', async (event) => {
            event.preventDefault();
            const values = {
                fields: fields.map((_, i) => form.elements[`f${i}`].value),
                checks: checks.map((_, i) => form.elements[`c${i}`].checked),
            };
            if (onSubmit) {
                submit.disabled = true;
                submit.classList.add('is-busy');
                try {
                    await onSubmit(values);
                } catch (failure) {
                    error.textContent = failure.message;
                    error.hidden = false;
                    submit.disabled = false;
                    submit.classList.remove('is-busy');
                    $('input', element)?.select();
                    return;
                }
            }

            finish(values);
        });

        element.showModal();
        ($('input', element) ?? submit).focus();
    });
}

export const confirmDialog = (options) => dialog({ ...options, fields: [] }).then((result) => result !== null);

// ---- Popover menu ------------------------------------------------------------------

let openMenu = null;

export function closeMenu() {
    if (openMenu) {
        openMenu.trigger?.setAttribute('aria-expanded', 'false');
        openMenu.element.remove();
        openMenu = null;
    }
}

/** Opens a menu under `trigger`; items: { label, icon, danger, action } or 'separator'. */
export function menu(trigger, items) {
    const reopen = openMenu?.trigger === trigger;
    closeMenu();
    if (reopen) {
        return;
    }

    const element = document.createElement('div');
    element.className = 'menu';
    element.setAttribute('role', 'menu');
    element.innerHTML = items.map((item, i) => item === 'separator'
        ? '<hr>'
        : `<button role="menuitem" data-index="${i}" class="${item.danger ? 'danger' : ''}">${item.icon ? icon(item.icon) : ''}${escapeHtml(item.label)}</button>`).join('');
    document.body.append(element);

    const rect = trigger.getBoundingClientRect();
    const left = Math.min(rect.left, window.innerWidth - element.offsetWidth - 8);
    const below = rect.bottom + 4 + element.offsetHeight < window.innerHeight;
    element.style.left = `${left}px`;
    element.style.top = `${below ? rect.bottom + 4 : rect.top - element.offsetHeight - 4}px`;
    element.style.transformOrigin = below ? 'top left' : 'bottom left';

    trigger.setAttribute('aria-expanded', 'true');
    openMenu = { element, trigger };
    element.addEventListener('click', (event) => {
        const button = event.target.closest('button[data-index]');
        if (button) {
            closeMenu();
            items[Number(button.dataset.index)].action();
        }
    });
    $('button', element)?.focus();
}

document.addEventListener('pointerdown', (event) => {
    if (openMenu && !openMenu.element.contains(event.target) && !openMenu.trigger.contains(event.target)) {
        closeMenu();
    }
});
document.addEventListener('keydown', (event) => {
    if (!openMenu) return;
    const buttons = $$('button', openMenu.element);
    const index = buttons.indexOf(document.activeElement);
    if (event.key === 'Escape') {
        const trigger = openMenu.trigger;
        closeMenu();
        trigger.focus();
    } else if (event.key === 'ArrowDown' || event.key === 'ArrowUp') {
        event.preventDefault();
        buttons[(index + (event.key === 'ArrowDown' ? 1 : buttons.length - 1)) % buttons.length].focus();
    }
});

// ---- Toast -----------------------------------------------------------------------

let toastTimer;
export function toast(text, problem = false) {
    const element = $('#toast');
    $('#toast-text').textContent = text;
    $('use', element).setAttribute('href', problem ? '#i-alert' : '#i-check');
    element.classList.toggle('toast--problem', problem);
    element.hidden = false;
    element.classList.remove('is-entering');
    void element.offsetWidth;
    element.classList.add('is-entering');
    clearTimeout(toastTimer);
    toastTimer = setTimeout(() => { element.hidden = true; }, problem ? 5000 : 2400);
}
