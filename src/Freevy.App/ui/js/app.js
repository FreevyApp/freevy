// Freevy desktop UI: renders the app state from C# (or the demo backend) and sends user actions back.
import { call, on, isDemo } from './bridge.js';
import { $, $$, escapeHtml, icon, number, timeAgo, dialog, confirmDialog, menu, toast } from './ui.js';

// Order and names for the theme picker; the colors live in css/themes.css.
const THEMES = [
    ['freevy', 'Freevy', 'dark'], ['midnight', 'Midnight', 'dark'], ['ocean', 'Ocean', 'dark'],
    ['violet', 'Violet', 'dark'], ['synthwave', 'Synthwave', 'dark'], ['rose', 'Rose', 'dark'],
    ['crimson', 'Crimson', 'dark'], ['ember', 'Ember', 'dark'], ['gold', 'Gold', 'dark'],
    ['forest', 'Forest', 'dark'], ['graphite', 'Graphite', 'dark'],
    ['daylight', 'Daylight', 'light'], ['sand', 'Sand', 'light'], ['mint', 'Mint', 'light'],
];
const DEFAULT_DELIMITERS = ',.?!()<>[]{}:;"/\\-\' \\t\\n';
const ALL = '__all__';
const ROW_HEIGHT = 56;
const folderColors = ['oklch(0.84 0.13 182)', 'oklch(0.78 0.12 290)', 'oklch(0.80 0.12 230)', 'oklch(0.85 0.12 85)', 'oklch(0.78 0.14 350)', 'oklch(0.82 0.13 150)', 'oklch(0.80 0.12 40)'];
const colorFor = (name) => folderColors[[...name].reduce((sum, c) => sum + c.charCodeAt(0), 0) % folderColors.length];

const state = {
    app: null,
    snippets: [],
    visible: [],
    selectedId: null,
    folder: null, // null = all snippets, '' = snippets without a folder
    query: '',
    view: 'snippets',
    draft: null,
    original: '',
    sftpProbe: null,
    history: null, // null = not loaded, 'loading', or the list of versions
};
// Read-only handle for automated UI tests and debugging.
window.freevyState = state;

// ---- Helpers ----------------------------------------------------------------------

async function run(action, success) {
    try {
        const result = await action();
        if (success) {
            toast(typeof success === 'function' ? success(result) : success);
        }

        return result;
    } catch (failure) {
        toast(failure.message, true);
        return undefined;
    }
}

const settings = () => state.app.settings;
const isMasked = (s) => settings().maskFoldersContaining && s.folder.toLowerCase().includes(settings().maskFoldersContaining.toLowerCase());
const firstLine = (text) => (text ?? '').split(String.fromCharCode(10))[0];
const plural = (n, word) => `${number.format(n)} ${word}${n === 1 ? '' : 's'}`;

async function refreshState() {
    state.app = await call('getState');
}

async function refreshSnippets() {
    state.snippets = await call('getSnippets');
    state.snippets.sort((a, b) => a.abbreviations[0].localeCompare(b.abbreviations[0], undefined, { sensitivity: 'base' }));
}

function unlockDialog(reason = 'Enter your master password to use protected snippets.') {
    return dialog({
        title: 'Unlock protected snippets',
        body: escapeHtml(reason),
        fields: [{ label: 'Master password', type: 'password', autocomplete: 'current-password' }],
        confirm: 'Unlock',
        onSubmit: async ({ fields }) => {
            if (!(await call('unlock', { password: fields[0] }))) {
                throw new Error('Wrong master password.');
            }
        },
    }).then((result) => result !== null);
}

// ---- Theme ------------------------------------------------------------------------

function applyTheme(id, animate) {
    if (!THEMES.some((t) => t[0] === id)) {
        id = 'freevy';
    }

    const swap = () => {
        document.documentElement.dataset.theme = id;
        $$('.theme-card').forEach((card) => {
            const selected = card.dataset.themeChoice === id;
            card.setAttribute('aria-checked', String(selected));
            card.tabIndex = selected ? 0 : -1;
        });
        tintWindowFrame();
    };
    const reduce = window.matchMedia('(prefers-reduced-motion: reduce)').matches;
    if (animate && !reduce && document.startViewTransition) {
        document.startViewTransition(swap);
    } else {
        swap();
    }
}

/** The native resize frame around the web view takes the theme's background color. */
function tintWindowFrame() {
    const canvas = document.createElement('canvas');
    canvas.width = canvas.height = 1;
    const context = canvas.getContext('2d');
    context.fillStyle = getComputedStyle(document.body).backgroundColor;
    context.fillRect(0, 0, 1, 1);
    const [r, g, b] = context.getImageData(0, 0, 1, 1).data;
    const hex = '#' + [r, g, b].map((v) => v.toString(16).padStart(2, '0')).join('');
    call('window', { action: `frame:${hex}` });
}

// ---- Chrome: chips, sidebar, stats ----------------------------------------------

function renderChrome() {
    const app = state.app;
    const expansion = $('#chip-expansion');
    expansion.className = `chip ${app.settings.expansionEnabled ? 'chip--accent' : 'chip--off'}`;
    $('span:last-child', expansion).textContent = app.settings.expansionEnabled ? 'Expanding' : 'Paused';

    const sync = $('#chip-sync');
    $('span', sync).textContent = !app.sync.signedIn ? 'Not syncing'
        : app.sync.running ? 'Syncing…'
        : app.sync.error ? 'Sync problem'
        : app.sync.lastSync ? `Synced ${timeAgo(app.sync.lastSync)}` : 'Sync pending';
    sync.title = app.sync.error ?? (app.sync.signedIn ? 'Sync now' : 'Sign in to sync');
    sync.classList.toggle('chip--warning', Boolean(app.sync.error));
    sync.classList.toggle('is-busy', app.sync.running);

    const vault = $('#chip-vault');
    vault.hidden = !app.vault.configured;
    vault.className = `chip ${app.vault.unlocked ? 'chip--accent' : 'chip--warning'}`;
    $('use', vault).setAttribute('href', app.vault.unlocked ? '#i-unlock' : '#i-lock');
    $('span', vault).textContent = app.vault.unlocked ? `Unlocked until ${app.vault.locksAt}` : 'Vault locked';

    $('#nav-count').textContent = number.format(state.snippets.length);
    $('#saved-today').textContent = plural(app.stats.today.keystrokesSaved, 'keystroke');
    $('#saved-detail').textContent = `${plural(app.stats.today.expansions, 'expansion')} today · ${number.format(app.stats.totalKeystrokesSaved)} in total`;
}

function renderFolders() {
    const counts = new Map();
    state.snippets.forEach((s) => counts.set(s.folder, (counts.get(s.folder) ?? 0) + 1));
    const folders = [...counts.keys()].filter(Boolean).sort((a, b) => a.localeCompare(b));
    const selected = state.folder === null ? ALL : state.folder;
    const row = (value, label, count, locked, withMenu) => `
        <div class="folder-row">
            <button class="folder-item" data-folder="${escapeHtml(value)}" aria-pressed="${selected === value}">
                <span class="dot" data-color="${value === ALL ? '' : colorFor(value || '?')}"></span>
                <span class="name">${escapeHtml(label)}</span>
                <span class="count">${locked ? icon('lock') : ''}${number.format(count)}</span>
            </button>
            ${withMenu ? `<button class="folder-menu-button" data-folder-menu="${escapeHtml(value)}" aria-label="Actions for ${escapeHtml(label)}" aria-haspopup="menu" aria-expanded="false">${icon('more')}</button>` : ''}
        </div>`;
    $('#folders').innerHTML = row(ALL, 'All snippets', state.snippets.length, false, false)
        + (counts.get('') ? row('', 'No folder', counts.get(''), false, false) : '')
        + folders.map((f) => row(f, f, counts.get(f), state.snippets.some((s) => s.folder === f && s.protected), true)).join('');
    // Set through the DOM: the page's CSP forbids inline style attributes.
    $$('.folder-item .dot[data-color]').forEach((dot) => {
        if (dot.dataset.color) {
            dot.style.setProperty('--folder-color', dot.dataset.color);
        }
    });
}

function folderMenu(trigger, folder) {
    const count = state.snippets.filter((s) => s.folder === folder || s.folder.startsWith(folder + '/')).length;
    menu(trigger, [
        { label: 'Rename folder…', icon: 'pencil', action: () => renameFolder(folder) },
        { label: 'Protect all snippets', icon: 'lock', action: () => protectFolder(folder, true) },
        { label: 'Remove protection', icon: 'unlock', action: () => protectFolder(folder, false) },
        'separator',
        { label: `Delete folder (${count})…`, icon: 'trash', danger: true, action: () => deleteFolder(folder, count) },
    ]);
}

async function renameFolder(folder) {
    const result = await dialog({
        title: 'Rename folder',
        body: 'Use / for subfolders, for example Games/WoW.',
        fields: [{ label: 'Folder name', value: folder }],
        confirm: 'Rename',
        onSubmit: ({ fields }) => call('renameFolder', { folder, name: fields[0] }),
    });
    if (result) {
        if (state.folder === folder) state.folder = result.fields[0].trim().replace(/^\/+|\/+$/g, '');
        toast('Folder renamed');
    }
}

async function protectFolder(folder, protect) {
    if (!state.app.vault.configured) {
        toast('Set a master password first, on the Master password page.', true);
        return;
    }

    if (!state.app.vault.unlocked && !(await unlockDialog())) {
        return;
    }

    await run(() => call('protectFolder', { folder, protect }), (n) => `${plural(n, 'snippet')} ${protect ? 'protected' : 'unprotected'}`);
}

async function deleteFolder(folder, count) {
    if (await confirmDialog({ title: `Delete “${folder}”?`, body: `This deletes the folder and its ${plural(count, 'snippet')}. Backups keep older copies.`, confirm: 'Delete folder', danger: true })) {
        if (state.folder === folder) state.folder = null;
        await run(() => call('deleteFolder', { folder }), 'Folder deleted');
    }
}

// ---- Snippet list (virtualized) -----------------------------------------------------

function filterSnippets() {
    const query = state.query.trim().toLowerCase();
    state.visible = state.snippets.filter((s) =>
        (state.folder === null || s.folder === state.folder || (state.folder !== '' && s.folder.startsWith(state.folder + '/')))
        && (!query
            || s.abbreviations.some((a) => a.toLowerCase().includes(query))
            || s.description.toLowerCase().includes(query)
            || (!s.protected && !isMasked(s) && (s.expansion ?? '').toLowerCase().includes(query))));
}

function renderList() {
    filterSnippets();
    const list = $('#list');
    const spacer = $('#list-spacer');
    $('#list-count').textContent = plural(state.visible.length, 'snippet');

    if (state.visible.length === 0) {
        spacer.style.height = 'auto';
        spacer.innerHTML = state.snippets.length === 0
            ? `<div class="empty">${icon('keyboard')}<strong>No snippets yet</strong><span>Create your first one, or import your Breevy library in Settings.</span></div>`
            : `<div class="empty">${icon('search')}<strong>Nothing matches${state.query ? ` “${escapeHtml(state.query)}”` : ' here'}</strong><span>Try another word or folder, or create a new snippet.</span></div>`;
        return;
    }

    spacer.style.height = `${state.visible.length * ROW_HEIGHT}px`;
    const first = Math.max(0, Math.floor(list.scrollTop / ROW_HEIGHT) - 6);
    const last = Math.min(state.visible.length, first + Math.ceil(list.clientHeight / ROW_HEIGHT) + 12);
    let html = '';
    for (let i = first; i < last; i++) {
        const s = state.visible[i];
        const primary = s.protected ? `<span class="locked">${icon('lock')}Protected</span>`
            : isMasked(s) ? '<span class="masked">Hidden</span>'
            : escapeHtml((s.expansion ?? '').split('\n')[0] || '(empty)');
        html += `
            <button class="snippet-row" role="option" data-id="${s.id}" aria-selected="${s.id === state.selectedId}" tabindex="-1" data-top="${i * ROW_HEIGHT}">
                <span class="abbr">${escapeHtml(s.abbreviations[0])}</span>
                <span class="snippet-text">
                    <span class="primary">${primary}</span>
                    <span class="secondary">${escapeHtml(s.description || s.folder || 'No folder')}${s.abbreviations.length > 1 ? ` · +${s.abbreviations.length - 1}` : ''}</span>
                </span>
            </button>`;
    }

    spacer.innerHTML = html;
    $$('.snippet-row', spacer).forEach((row) => { row.style.top = `${row.dataset.top}px`; });
}

function scrollToSelected() {
    const index = state.visible.findIndex((s) => s.id === state.selectedId);
    if (index < 0) return;
    const list = $('#list');
    const top = index * ROW_HEIGHT;
    if (top < list.scrollTop) list.scrollTop = top;
    else if (top + ROW_HEIGHT > list.scrollTop + list.clientHeight) list.scrollTop = top + ROW_HEIGHT - list.clientHeight;
}

// ---- Editor -----------------------------------------------------------------------

const isDirty = () => state.draft !== null && JSON.stringify(state.draft) !== state.original;

async function confirmLeave() {
    if (!isDirty()) return true;
    return confirmDialog({ title: 'Discard changes?', body: 'This snippet has unsaved changes.', confirm: 'Discard', cancel: 'Keep editing', danger: true });
}

async function select(id) {
    if (id === state.selectedId && state.draft) return;
    if (!(await confirmLeave())) return;
    state.selectedId = id;
    const s = state.snippets.find((x) => x.id === id);
    state.draft = s ? { ...structuredClone(s), expansion: s.protected ? null : s.expansion } : null;
    if (s?.protected && state.app.vault.unlocked) {
        state.draft.expansion = await call('revealSnippet', { id }).catch(() => null);
    }

    state.original = JSON.stringify(state.draft);
    renderList();
    renderEditor();
}

async function newSnippet() {
    if (!(await confirmLeave())) return;
    const s = settings();
    state.selectedId = null;
    state.draft = { id: null, abbreviations: [], expansion: '', description: '', folder: state.folder ?? '', trigger: s.defaultTrigger, wordStart: s.defaultWordStart, case: s.defaultCase, protected: false };
    state.original = JSON.stringify(state.draft);
    showView('snippets');
    renderEditor();
    $('#editor .tags input')?.focus();
}

function segmented(name, key, value, options) {
    return `<div class="segmented" role="radiogroup" aria-label="${name}" data-segment="${key}">${options.map(([v, label]) =>
        `<button type="button" role="radio" data-value="${v}" aria-checked="${v === value}" tabindex="${v === value ? 0 : -1}">${label}</button>`).join('')}</div>`;
}

function renderEditor() {
    const d = state.draft;
    const editor = $('#editor');
    if (!d) {
        editor.innerHTML = `<div class="empty">${icon('keyboard')}<strong>Pick a snippet</strong><span>Or create a new one with <kbd>Ctrl N</kbd>.</span></div>`;
        return;
    }

    const folders = [...new Set(state.snippets.map((s) => s.folder).filter(Boolean))].sort();
    const locked = d.protected && d.expansion === null;
    editor.innerHTML = `
        <div class="editor-card">
            <div class="editor-head">
                <div><div class="eyebrow">${escapeHtml(d.folder || 'No folder')}</div><h1 class="editor-title">${escapeHtml(d.abbreviations[0] ?? 'New snippet')}<span class="dirty-dot" title="Unsaved changes" hidden></span></h1></div>
                <div class="editor-actions">
                    ${d.id ? `<button class="btn btn--ghost" data-action="duplicate">${icon('copy')}Duplicate</button><button class="btn btn--ghost btn--danger" data-action="delete">${icon('trash')}Delete</button>` : ''}
                    <button class="btn btn--primary" data-action="save" title="Ctrl S">Save</button>
                </div>
            </div>
            <div class="fields">
                <label class="field"><span>Abbreviations</span>
                    <div class="tags">${d.abbreviations.map((a, i) => `<span class="tag">${escapeHtml(a)}<button type="button" data-remove-tag="${i}" aria-label="Remove ${escapeHtml(a)}">${icon('x')}</button></span>`).join('')}<input data-field="tag" placeholder="${d.abbreviations.length ? 'Add…' : 'Type an abbreviation, press Enter'}" aria-label="Add abbreviation" spellcheck="false"></div>
                </label>
                <label class="field"><span>Description</span><input class="input" data-field="description" value="${escapeHtml(d.description)}" placeholder="What is this for?"></label>
                <label class="field field--wide"><span>Expansion</span>
                    ${locked
                        ? `<div class="empty">${icon('lock')}<strong>Protected snippet</strong><span>Unlock with your master password to view or edit it.</span><button type="button" class="btn btn--primary btn--small" data-action="unlock">Unlock</button></div>`
                        : `<textarea class="textarea" data-field="expansion" spellcheck="false" placeholder="The text Freevy types for you">${escapeHtml(d.expansion ?? '')}</textarea>`}
                </label>
                <label class="field"><span>Folder</span><input class="input" data-field="folder" list="folder-options" value="${escapeHtml(d.folder)}" placeholder="No folder"><datalist id="folder-options">${folders.map((f) => `<option value="${escapeHtml(f)}">`).join('')}</datalist></label>
            </div>
            <div class="options">
                <div class="option-card"><span class="label">Expand</span>${segmented('Expand', 'trigger', d.trigger, [['after', 'After space'], ['instant', 'Instantly']])}
                    <label class="check"><input type="checkbox" data-field="wordStart" ${d.wordStart ? 'checked' : ''}><span>Only at the start of a word</span></label></div>
                <div class="option-card"><span class="label">Case</span>${segmented('Case', 'case', d.case, [['exact', 'Exact'], ['ignore', 'Ignore'], ['adapt', 'Adapt']])}</div>
                <div class="option-card option-card--row"><div><div class="label">Protected</div><div class="hint">Needs master password</div></div><button type="button" class="switch" role="switch" data-field="protected" aria-checked="${d.protected}" aria-label="Protected"></button></div>
            </div>
            <div class="preview"><div class="label">Preview</div><p id="preview-line"></p></div>
        </div>`;
    updateEditorChrome();
}

function updateEditorChrome() {
    const d = state.draft;
    if (!d) return;
    $('#editor .dirty-dot')?.toggleAttribute('hidden', !isDirty());
    const caseNote = d.case === 'adapt' ? ' Type it in capitals to get capitals.' : '';
    $('#preview-line').innerHTML = `You type <kbd>${escapeHtml(d.abbreviations[0] ?? '…')}${d.trigger === 'after' ? '␣' : ''}</kbd> and Freevy types the expansion.${caseNote}`;
}

function addTag(input) {
    const values = input.value.split(/[|,]/).map((v) => v.trim()).filter(Boolean);
    if (values.length === 0) return false;
    values.forEach((v) => { if (!state.draft.abbreviations.includes(v)) state.draft.abbreviations.push(v); });
    input.value = '';
    renderEditor();
    $('#editor .tags input').focus();
    return true;
}

async function save() {
    const d = state.draft;
    if (!d) return;
    const tagInput = $('#editor .tags input');
    if (tagInput?.value.trim()) addTag(tagInput);
    if (d.protected && d.expansion !== null && !state.app.vault.configured) {
        toast('Set a master password first, on the Master password page.', true);
        return;
    }

    const needsKey = (d.protected && d.expansion !== null) || (!d.protected && d.expansion === null);
    if (needsKey && !state.app.vault.unlocked && !(await unlockDialog('Saving this snippet needs your master password.'))) return;

    const saved = await run(() => call('saveSnippet', d));
    if (!saved) return;
    state.selectedId = saved.id;
    state.draft = { ...saved, expansion: saved.protected ? (d.expansion ?? null) : saved.expansion };
    state.original = JSON.stringify(state.draft);
    await refreshSnippets();
    renderAllSnippetParts();
    scrollToSelected();
    toast('Snippet saved');
}

async function duplicate() {
    const d = state.draft;
    if (d.protected && d.expansion === null) {
        if (!(await unlockDialog())) return;
        d.expansion = await call('revealSnippet', { id: d.id });
    }

    state.selectedId = null;
    state.draft = { ...structuredClone(d), id: null, description: d.description ? `${d.description} (copy)` : 'Copy' };
    state.original = '';
    renderList();
    renderEditor();
    toast('Duplicated. Change it, then save.');
}

async function remove() {
    const d = state.draft;
    if (await confirmDialog({ title: `Delete “${d.abbreviations[0]}”?`, body: 'Backups keep older copies.', confirm: 'Delete', danger: true })) {
        await run(() => call('deleteSnippets', { ids: [d.id] }), 'Snippet deleted');
        state.draft = null;
        state.selectedId = null;
        await refreshSnippets();
        renderAllSnippetParts();
    }
}

function renderAllSnippetParts() {
    renderFolders();
    renderList();
    renderEditor();
    renderChrome();
}

// ---- Pages --------------------------------------------------------------------------

const row = (title, hint, control) => `<div class="row"><div class="text"><strong>${title}</strong>${hint ? `<span>${hint}</span>` : ''}</div><div class="control">${control}</div></div>`;
const toggle = (key, value, label) => `<button type="button" class="switch" role="switch" data-setting="${key}" aria-checked="${value}" aria-label="${escapeHtml(label)}"></button>`;
const numberInput = (key, value, min, max) => `<input class="input input--narrow" type="number" data-setting="${key}" value="${value}" min="${min}" max="${max}">`;

function renderBackups() {
    const b = settings().backup;
    const sftp = b.sftp;
    const st = state.app.backup;
    const probe = state.sftpProbe;
    const status = st.running ? 'Backing up…' : st.lastRun ? `Last backup ${timeAgo(st.lastRun)}. ${escapeHtml(st.result)}` : 'No backup has run yet.';
    const failed = st.lastRun && st.lastRun !== st.lastSuccess;
    $('[data-view="backups"]').innerHTML = `<div class="page-narrow">
        <h1 class="page-title">Backups</h1>
        <p class="page-lead">Zip files with your snippets and the encrypted vault. Protected snippets stay encrypted inside them.</p>
        <div class="actions">
            <button class="btn btn--primary ${st.running ? 'is-busy' : ''}" data-action="backup-now" ${st.running ? 'disabled' : ''}>${icon('history')}Back up now</button>
            <button class="btn" data-action="restore">${icon('upload')}Restore from backup…</button>
        </div>
        <p class="inline-note u-mt-3 ${failed ? 'inline-note--error' : ''}">${status}</p>

        ${renderHistoryGroup()}

        <div class="group">
            <div class="group-title">Schedule</div>
            ${row('Back up automatically', 'On a timer and after changes, in the background.', toggle('backup.enabled', b.enabled, 'Back up automatically'))}
            ${row('Every', 'Hours between backups. 0 means only after changes.', `${numberInput('backup.intervalHours', b.intervalHours, 0, 720)}<span class="inline-note">hours</span>`)}
            ${row('After changes', 'A few minutes after you edit snippets.', toggle('backup.onChange', b.onChange, 'Back up after changes'))}
            ${row('Keep', 'Older backups are deleted in each place.', `${numberInput('backup.keepCount', b.keepCount, 1, 1000)}<span class="inline-note">newest</span>`)}
        </div>

        <div class="group">
            <div class="group-title">This PC</div>
            ${row('Local folder', escapeHtml(b.localDirectory), `<button class="btn btn--small" data-choose="backup.localDirectory">Change…</button>${toggle('backup.localEnabled', b.localEnabled, 'Local backups')}`)}
        </div>

        <div class="group">
            <div class="group-title">Synced folder</div>
            <div class="group-lead">Google Drive for Desktop, OneDrive or Dropbox upload this folder for you.</div>
            ${row('Folder', b.folderDirectory ? escapeHtml(b.folderDirectory) : 'Not chosen yet', `<button class="btn btn--small" data-choose="backup.folderDirectory">Choose…</button>${toggle('backup.folderEnabled', b.folderEnabled, 'Synced folder backups')}`)}
        </div>

        <div class="group">
            <div class="group-title">SFTP server</div>
            <div class="group-lead">Any server you can reach over SSH. The password is stored encrypted for your Windows account.</div>
            ${row('Back up to this server', '', toggle('backup.sftp.enabled', sftp.enabled, 'SFTP backups'))}
            <div class="form-grid">
                <label class="field"><span>Host</span><input class="input" data-setting="backup.sftp.host" value="${escapeHtml(sftp.host)}" placeholder="backup.example.com" spellcheck="false"></label>
                <label class="field"><span>Port</span><input class="input" type="number" data-setting="backup.sftp.port" value="${sftp.port}" min="1" max="65535"></label>
                <label class="field"><span>User</span><input class="input" data-setting="backup.sftp.username" value="${escapeHtml(sftp.username)}" spellcheck="false"></label>
                <label class="field"><span>Password</span><input class="input" type="password" data-secret="password" placeholder="${sftp.hasPassword ? 'Saved (type to replace)' : 'Optional with a key'}" autocomplete="new-password"></label>
                <label class="field field--wide"><span>Private key</span><span class="input-with-button"><input class="input input--mono" data-setting="backup.sftp.privateKeyPath" value="${escapeHtml(sftp.privateKeyPath)}" placeholder="Optional OpenSSH or PuTTY key file"><button class="btn btn--small" data-choose-file="backup.sftp.privateKeyPath">Browse…</button></span></label>
                <label class="field"><span>Key passphrase</span><input class="input" type="password" data-secret="passphrase" placeholder="${sftp.hasPassphrase ? 'Saved (type to replace)' : 'Only if the key has one'}" autocomplete="new-password"></label>
                <label class="field"><span>Remote folder</span><input class="input input--mono" data-setting="backup.sftp.remoteDirectory" value="${escapeHtml(sftp.remoteDirectory)}"></label>
            </div>
            <div class="group-actions">
                <button class="btn btn--small" data-action="test-sftp">${icon('server')}Test connection</button>
                ${sftp.hostKeyFingerprint
                    ? `<span class="inline-note inline-note--ok">Trusted host key<span class="fingerprint">SHA256:${escapeHtml(sftp.hostKeyFingerprint)}</span></span>`
                    : '<span class="inline-note inline-note--warn">Host key not trusted yet. Test the connection to trust it.</span>'}
            </div>
            ${probe ? `<div class="group-actions"><span class="inline-note ${probe.changed ? 'inline-note--error' : ''}">${probe.trusted ? 'Connected. The host key matches the trusted one.' : probe.changed ? 'Warning: this server presented a different host key than the trusted one.' : 'Connected. Compare this host key with your server before trusting it:'}<span class="fingerprint">SHA256:${escapeHtml(probe.fingerprint)}</span></span>${probe.trusted ? '' : `<button class="btn btn--small ${probe.changed ? 'btn--danger' : 'btn--primary'}" data-action="trust-key">Trust this key</button>`}</div>` : ''}
        </div>
    </div>`;
}

function renderHistoryGroup() {
    if (!state.app.sync.signedIn) {
        return `<div class="group"><div class="group-title">Server history</div><div class="group-lead">Sign in on the Sync &amp; account page and the server keeps every earlier version of your snippets for 30 days, encrypted.</div><div class="group-actions"></div></div>`;
    }

    const history = state.history;
    let body;
    if (history === null) {
        body = `<div class="group-actions"><button class="btn btn--small" data-action="load-history">${icon('history')}Show server history</button></div>`;
    } else if (history === 'loading') {
        body = '<div class="group-actions"><span class="inline-note is-busy">Loading and decrypting on this PC…</span></div>';
    } else if (history.length === 0) {
        body = '<div class="group-actions"><span class="inline-note">No earlier versions in the last 30 days.</span></div>';
    } else {
        body = history.slice(0, 200).map((h) => row(
            `<span class="abbr">${escapeHtml(h.snippet.abbreviations[0])}</span> ${h.snippet.protected ? `<span class="locked">${icon('lock')}Protected</span>` : escapeHtml(firstLine(h.snippet.expansion).slice(0, 60))}`,
            `${h.wasDeleted ? (h.existsNow ? 'Deleted, then created again' : 'Deleted') : 'Earlier version'} · replaced ${timeAgo(h.replacedAt)}`,
            `<button class="btn btn--small" data-restore="${escapeHtml(h.key)}">${icon('history')}Restore</button>`)).join('');
    }

    return `<div class="group"><div class="group-title">Server history</div><div class="group-lead">Earlier versions of changed or deleted snippets from the last 30 days. Decrypted here; the server cannot read them.</div>${body}</div>`;
}

function renderAccount() {
    const app = state.app;
    const sync = settings().sync;
    const page = $('[data-view="account"]');
    if (!app.sync.signedIn) {
        const customServer = sync.serverUrl !== sync.defaultServer;
        page.innerHTML = `<div class="page-narrow">
            <h1 class="page-title">Sync &amp; account</h1>
            <p class="page-lead">Sign in to keep your snippets on every computer. They are encrypted on this PC before upload; we cannot read them.</p>
            <form class="card signin" id="signin-form">
                <label class="field"><span>Email</span><input class="input" name="email" type="email" value="${escapeHtml(sync.email)}" required autocomplete="username" spellcheck="false"></label>
                <label class="field"><span>Password</span><input class="input" name="password" type="password" required autocomplete="current-password"></label>
                <label class="field"><span>Name of this computer</span><input class="input" name="device" value="${escapeHtml(sync.deviceName)}" required></label>
                <label class="field" id="server-field" ${customServer ? '' : 'hidden'}><span>Server</span><input class="input input--mono" name="server" value="${escapeHtml(sync.serverUrl)}" spellcheck="false"></label>
                <div class="error" hidden></div>
                <div class="actions">
                    <button class="btn btn--primary" type="submit">Sign in</button>
                    ${customServer ? '' : '<button class="btn btn--ghost" type="button" data-action="custom-server">Use another server</button>'}
                </div>
                <p class="inline-note u-mt-4">No account yet? <button class="link" type="button" data-action="open-register">Create one on freevy.app</button></p>
            </form>
        </div>`;
        return;
    }

    page.innerHTML = `<div class="page-narrow">
        <h1 class="page-title">Sync &amp; account</h1>
        <p class="page-lead">Your snippets are encrypted on this PC before they are uploaded.</p>
        <div class="card account">
            <div class="account-head"><span class="avatar">${escapeHtml((sync.email[0] ?? '?').toUpperCase())}</span><div><strong>${escapeHtml(sync.email)}</strong><span>This computer: ${escapeHtml(sync.deviceName)}</span></div></div>
            <div class="stats">
                <div class="stat"><strong>${number.format(state.snippets.length)}</strong><span>snippets</span></div>
                <div class="stat"><strong>${app.sync.running ? '…' : app.sync.lastSync ? timeAgo(app.sync.lastSync) : 'not yet'}</strong><span>last sync</span></div>
                <div class="stat stat--accent"><strong>${sync.intervalMinutes} min</strong><span>sync interval</span></div>
            </div>
            ${app.sync.error ? `<p class="inline-note inline-note--error u-mt-4">${escapeHtml(app.sync.error)}</p>` : ''}
            <div class="actions">
                <button class="btn btn--primary ${app.sync.running ? 'is-busy' : ''}" data-action="sync-now" ${app.sync.running ? 'disabled' : ''}>${icon('refresh')}Sync now</button>
                <button class="btn" data-action="open-account">${icon('external')}Devices on the website</button>
                <button class="btn btn--ghost" data-action="sign-out">${icon('logout')}Sign out</button>
            </div>
        </div>
        <div class="group">
            ${row('Sync every', 'Freevy also syncs a few seconds after you change something.', `${numberInput('syncIntervalMinutes', sync.intervalMinutes, 1, 1440)}<span class="inline-note">minutes</span>`)}
        </div>
    </div>`;
}

function renderSecurity() {
    const app = state.app;
    const protectedCount = state.snippets.filter((s) => s.protected).length;
    const s = settings();
    const main = !app.vault.configured
        ? `<form class="card signin" id="master-form">
                <h3 class="u-m0">Set a master password</h3>
                <p class="inline-note u-mt-1">Protected snippets are encrypted with it (AES-256). There is no recovery: if you forget it, protected snippets cannot be opened, also not from backups.</p>
                <label class="field u-mt-4"><span>Master password (at least 8 characters)</span><input class="input" name="password" type="password" required minlength="8" autocomplete="new-password"></label>
                <label class="field"><span>Repeat</span><input class="input" name="repeat" type="password" required autocomplete="new-password"></label>
                <label class="check u-mt-3"><input type="checkbox" name="protectFolders" checked><span>Protect snippets in folders whose name contains “password”</span></label>
                <div class="error" hidden></div>
                <div class="actions"><button class="btn btn--primary" type="submit">${icon('shield')}Set master password</button></div>
            </form>`
        : `<div class="cards">
                <div class="card ${app.vault.unlocked ? 'card--active' : ''}"><div class="card-icon">${icon(app.vault.unlocked ? 'unlock' : 'lock')}</div><h3>${app.vault.unlocked ? `Unlocked until ${app.vault.locksAt}` : 'Locked'}</h3><div class="meta">${plural(protectedCount, 'protected snippet')}</div></div>
            </div>
            <div class="actions">
                ${app.vault.unlocked
                    ? `<button class="btn btn--primary" data-action="lock">${icon('lock')}Lock now</button>`
                    : `<button class="btn btn--primary" data-action="unlock">${icon('unlock')}Unlock</button>`}
                <button class="btn" data-action="change-master">${icon('key')}Change master password</button>
                <button class="btn btn--ghost btn--danger" data-action="remove-master">Remove master password</button>
            </div>`;

    $('[data-view="security"]').innerHTML = `<div class="page-narrow">
        <h1 class="page-title">Master password</h1>
        <p class="page-lead">Protected snippets (passwords, card numbers, anything secret) are encrypted on this PC and only unlock for a few minutes at a time.</p>
        ${main}
        <div class="group">
            <div class="group-title">Unlocking</div>
            ${row('Stay unlocked for', 'After entering the master password.', `${numberInput('unlockMinutes', s.unlockMinutes, 1, 1440)}<span class="inline-note">minutes</span>`)}
            ${row('Lock when Windows locks', 'Forget the key as soon as you lock your PC.', toggle('lockWhenWindowsLocks', s.lockWhenWindowsLocks, 'Lock when Windows locks'))}
        </div>
    </div>`;
}

function renderSettings() {
    const s = settings();
    const themeCard = ([id, name]) => `
        <button class="theme-card" role="radio" data-theme-choice="${id}" aria-checked="${id === s.theme}" tabindex="${id === s.theme ? 0 : -1}">
            <span class="theme-preview" data-theme="${id}" aria-hidden="true">
                <span class="side"><i></i><i></i><i></i></span>
                <span class="main"><span class="panel"><i></i><i></i></span><span class="button"></span></span>
            </span>
            <span class="name">${name}${icon('check')}</span>
        </button>`;
    $('[data-view="settings"]').innerHTML = `<div class="page-narrow">
        <h1 class="page-title">Settings</h1>
        <p class="page-lead">How Freevy types, starts and looks.</p>

        <div class="group">
            <div class="group-title">General</div>
            ${row('Expand abbreviations', 'Pause Freevy without closing it. Also on the chip in the title bar.', toggle('expansionEnabled', s.expansionEnabled, 'Expand abbreviations'))}
            ${row('Start with Windows', 'Freevy starts in the tray when you sign in to Windows.', toggle('autostart', s.autostart, 'Start with Windows'))}
        </div>

        <div class="group">
            <div class="group-title">Typing</div>
            ${row('Delimiters', 'Characters that end a word. \\t is Tab, \\n is Enter.', `<input class="input input--medium input--mono" data-setting="delimiters" value="${escapeHtml(s.delimiters)}" spellcheck="false"><button class="btn btn--small btn--ghost" data-action="reset-delimiters">Reset</button>`)}
            ${row('Paste long expansions', 'Expansions longer than this are pasted instead of typed.', `${numberInput('clipboardThreshold', s.clipboardThreshold, 1, 1000000)}<span class="inline-note">characters</span>`)}
            ${row('New snippets expand', '', segmented('Default expand', 'defaultTrigger', s.defaultTrigger, [['after', 'After space'], ['instant', 'Instantly']]))}
            ${row('New snippets only at word start', '', toggle('defaultWordStart', s.defaultWordStart, 'Only at word start'))}
            ${row('New snippets case', '', segmented('Default case', 'defaultCase', s.defaultCase, [['exact', 'Exact'], ['ignore', 'Ignore'], ['adapt', 'Adapt']]))}
        </div>

        <div class="group">
            <div class="group-title">Privacy</div>
            ${row('Hide expansions in folders containing', 'Only hides text in the list. Use protected snippets to encrypt secrets.', `<input class="input input--medium" data-setting="maskFoldersContaining" value="${escapeHtml(s.maskFoldersContaining)}" placeholder="e.g. private">`)}
        </div>

        <h2 class="section-title">${icon('palette')}Theme</h2>
        <div class="theme-group-label">Dark</div>
        <div class="theme-grid" role="radiogroup" aria-label="Dark themes">${THEMES.filter((t) => t[2] === 'dark').map(themeCard).join('')}</div>
        <div class="theme-group-label">Light</div>
        <div class="theme-grid" role="radiogroup" aria-label="Light themes">${THEMES.filter((t) => t[2] === 'light').map(themeCard).join('')}</div>

        <div class="group">
            <div class="group-title">Your data</div>
            ${row('Import from Breevy', state.app.breevyAvailable ? 'Replaces your snippets with your Breevy library. The current ones are backed up first.' : 'No Breevy library found on this PC.', `<button class="btn btn--small" data-action="import-breevy" ${state.app.breevyAvailable ? '' : 'disabled'}>${icon('download')}Import</button>`)}
            ${row('Export snippets', 'A JSON file with all snippets; protected ones stay encrypted.', `<button class="btn btn--small" data-action="export">${icon('upload')}Export…</button>`)}
            ${row('Data folder', escapeHtml(state.app.dataDirectory), `<button class="btn btn--small" data-action="open-data">${icon('folder')}Open</button>`)}
        </div>
        <p class="inline-note about">Freevy ${escapeHtml(state.app.version ?? '')}${isDemo ? ' (demo data)' : ''}.</p>
    </div>`;
}

const pages = { backups: renderBackups, account: renderAccount, security: renderSecurity, settings: renderSettings };

function renderPage() {
    pages[state.view]?.();
}

function showView(name) {
    state.view = name;
    $$('.nav-item').forEach((b) => {
        if (b.dataset.nav === name) b.setAttribute('aria-current', 'page');
        else b.removeAttribute('aria-current');
    });
    $$('.view').forEach((view) => {
        const active = view.dataset.view === name;
        view.hidden = !active;
        view.classList.toggle('is-entering', active);
    });
    renderPage();
    if (name === 'snippets') renderList();
}

// ---- Settings changes -------------------------------------------------------------

/** "backup.sftp.host" + value becomes { backup: { sftp: { host: value } } } for saveSettings. */
function nested(path, value) {
    return path.split('.').reduceRight((inner, key) => ({ [key]: inner }), value);
}

async function saveSetting(path, value) {
    if (path === 'autostart') {
        await run(() => call('setAutostart', { enabled: value }), value ? 'Freevy starts with Windows' : 'Freevy no longer starts with Windows');
        await refreshState();
        renderPage();
        return;
    }

    const result = await run(() => call('saveSettings', nested(path, value)));
    if (result) {
        state.app = result;
        renderChrome();
    }
}

// ---- Events ---------------------------------------------------------------------------

document.addEventListener('click', async (event) => {
    const target = event.target.closest('button');
    if (!target) return;
    const action = target.dataset.action;

    if (target.dataset.nav) return showView(target.dataset.nav);
    if (target.dataset.window) return call('window', { action: target.dataset.window });
    if (target.matches('.folder-item')) {
        state.folder = target.dataset.folder === ALL ? null : target.dataset.folder;
        showView('snippets');
        renderFolders();
        $('#list').scrollTop = 0;
        return renderList();
    }
    if (target.dataset.folderMenu !== undefined) return folderMenu(target, target.dataset.folderMenu);
    if (target.matches('.snippet-row')) return select(target.dataset.id);
    if (target.id === 'new-snippet') return newSnippet();
    if (target.dataset.themeChoice) {
        applyTheme(target.dataset.themeChoice, true);
        settings().theme = target.dataset.themeChoice;
        return call('setTheme', { theme: target.dataset.themeChoice });
    }

    if (target.matches('.segmented button')) {
        const group = target.parentElement;
        $$('button', group).forEach((b) => { b.setAttribute('aria-checked', String(b === target)); b.tabIndex = b === target ? 0 : -1; });
        if (group.closest('#editor')) {
            state.draft[group.dataset.segment] = target.dataset.value;
            return updateEditorChrome();
        }

        return saveSetting(group.dataset.segment, target.dataset.value);
    }

    if (target.matches('.switch')) {
        const value = target.getAttribute('aria-checked') !== 'true';
        target.setAttribute('aria-checked', String(value));
        if (target.dataset.field === 'protected') {
            state.draft.protected = value;
            return updateEditorChrome();
        }

        return saveSetting(target.dataset.setting, value);
    }

    if (target.dataset.removeTag !== undefined) {
        state.draft.abbreviations.splice(Number(target.dataset.removeTag), 1);
        renderEditor();
        return;
    }

    if (target.dataset.choose) {
        const path = await call('chooseFolder', { title: 'Choose backup folder', initial: '' });
        if (path) await saveSetting(target.dataset.choose, path);
        return renderPage();
    }

    if (target.dataset.chooseFile) {
        const path = await call('chooseFile', { title: 'Choose SSH private key', filter: 'All files (*.*)|*.*', initial: '' });
        if (path) await saveSetting(target.dataset.chooseFile, path);
        return renderPage();
    }

    if (target.dataset.restore) {
        const version = state.history.find((h) => h.key === target.dataset.restore);
        const abbr = version.snippet.abbreviations[0];
        const body = version.existsNow ? `The current “${escapeHtml(abbr)}” is replaced by this version. The current one stays in the history.` : `“${escapeHtml(abbr)}” comes back as it was.`;
        if (await confirmDialog({ title: 'Restore this version?', body, confirm: 'Restore' })) {
            const restored = await run(() => call('restoreVersion', { key: version.key }), `Restored “${abbr}”`);
            if (restored) {
                state.history = null;
                renderBackups();
            }
        }
        return;
    }

    switch (action) {
        case 'save': return save();
        case 'load-history':
            state.history = 'loading';
            renderBackups();
            state.history = (await run(() => call('getHistory'))) ?? null;
            return renderBackups();
        case 'duplicate': return duplicate();
        case 'delete': return remove();
        case 'unlock':
            if (await unlockDialog()) {
                await refreshState();
                if (state.draft?.protected && state.draft.id) {
                    state.draft.expansion = await call('revealSnippet', { id: state.draft.id });
                    state.original = JSON.stringify(state.draft);
                    renderEditor();
                }
                renderChrome();
                renderPage();
            }
            return;
        case 'lock': return call('lock');
        case 'backup-now': return run(() => call('backupNow'));
        case 'restore': {
            const path = await call('chooseFile', { title: 'Restore from backup', filter: 'Freevy backups (*.zip)|*.zip', initial: settings().backup.localDirectory });
            if (path && await confirmDialog({ title: 'Restore this backup?', body: 'All current snippets are replaced. If it contains protected snippets, its master password applies again. The current state is saved first as a backup.', confirm: 'Restore', danger: true })) {
                await run(() => call('restoreBackup', { path }), (n) => `Restored ${plural(n, 'snippet')}`);
            }
            return;
        }
        case 'test-sftp': {
            target.classList.add('is-busy');
            target.disabled = true;
            state.sftpProbe = (await run(() => call('testSftp', collectSftpDraft()))) ?? null;
            return renderBackups();
        }
        case 'trust-key':
            await run(() => call('trustHostKey', { fingerprint: state.sftpProbe.fingerprint }), 'Host key trusted');
            state.sftpProbe = null;
            await refreshState();
            return renderBackups();
        case 'custom-server':
            $('#server-field').hidden = false;
            target.remove();
            return;
        case 'open-register': return call('openAccountPage', { path: 'register' });
        case 'open-account': return call('openAccountPage', { path: 'account' });
        case 'sync-now': return call('syncNow');
        case 'sign-out':
            if (await confirmDialog({ title: 'Sign this computer out?', body: 'Your snippets stay on this PC; they just stop syncing.', confirm: 'Sign out' })) {
                state.app = (await run(() => call('signOut'))) ?? state.app;
                renderPage();
                renderChrome();
            }
            return;
        case 'change-master': return changeMaster();
        case 'remove-master': return removeMaster();
        case 'reset-delimiters': {
            const input = $('[data-setting="delimiters"]');
            input.value = DEFAULT_DELIMITERS;
            return saveSetting('delimiters', input.value);
        }
        case 'import-breevy':
            if (await confirmDialog({ title: 'Import from Breevy?', body: 'Your current snippets are replaced by your Breevy library. They are backed up first.', confirm: 'Import' })) {
                await run(() => call('importBreevy'), (n) => `Imported ${plural(n, 'snippet')}`);
            }
            return;
        case 'export': {
            const path = await run(() => call('exportSnippets'));
            if (path) toast('Snippets exported');
            return;
        }
        case 'open-data': return call('openDataFolder');
    }
});

function collectSftpDraft() {
    const value = (key) => $(`[data-setting="backup.sftp.${key}"]`)?.value ?? '';
    return {
        backup: { sftp: { host: value('host'), port: Number(value('port')) || 22, username: value('username'), privateKeyPath: value('privateKeyPath'), remoteDirectory: value('remoteDirectory') } },
        password: $('[data-secret="password"]')?.value ?? '',
        passphrase: $('[data-secret="passphrase"]')?.value ?? '',
    };
}

async function changeMaster() {
    const result = await dialog({
        title: 'Change master password',
        body: 'Protected snippets are re-encrypted with the new password.',
        fields: [
            { label: 'Current master password', type: 'password', autocomplete: 'current-password' },
            { label: 'New master password (at least 8 characters)', type: 'password', autocomplete: 'new-password' },
            { label: 'Repeat new master password', type: 'password', autocomplete: 'new-password' },
        ],
        confirm: 'Change',
        onSubmit: async ({ fields }) => {
            if (fields[1] !== fields[2]) throw new Error('The new passwords do not match.');
            await call('setMasterPassword', { current: fields[0], password: fields[1] });
        },
    });
    if (result) toast('Master password changed');
}

async function removeMaster() {
    const count = state.snippets.filter((s) => s.protected).length;
    const result = await dialog({
        title: 'Remove master password?',
        body: `${plural(count, 'protected snippet')} will be stored as plain text again.`,
        fields: [{ label: 'Master password', type: 'password', autocomplete: 'current-password' }],
        confirm: 'Remove',
        danger: true,
        onSubmit: ({ fields }) => call('removeMasterPassword', { password: fields[0] }),
    });
    if (result) toast('Master password removed');
}

document.addEventListener('change', async (event) => {
    const input = event.target;
    if (input.dataset.setting && input.type !== 'checkbox') {
        const value = input.type === 'number' ? Number(input.value) : input.value;
        return saveSetting(input.dataset.setting, value);
    }

    if (input.dataset.secret) {
        await run(() => call('setSftpSecret', { [input.dataset.secret]: input.value }), input.value ? 'Saved securely' : 'Removed');
        input.value = '';
        await refreshState();
        return renderBackups();
    }

    if (input.dataset.field === 'wordStart') {
        state.draft.wordStart = input.checked;
        updateEditorChrome();
    }
});

document.addEventListener('input', (event) => {
    const input = event.target;
    if (input.id === 'search') {
        state.query = input.value;
        $('#list').scrollTop = 0;
        return renderList();
    }

    const field = input.dataset.field;
    if (field && state.draft && ['description', 'expansion', 'folder'].includes(field)) {
        state.draft[field] = input.value;
        updateEditorChrome();
        if (field === 'folder') $('#editor .eyebrow').textContent = input.value || 'No folder';
    }
});

document.addEventListener('submit', async (event) => {
    event.preventDefault();
    const form = event.target;
    if (form.closest('dialog')) return;
    const error = $('.error', form);
    const submit = $('[type="submit"]', form);
    error.hidden = true;
    submit.disabled = true;
    submit.classList.add('is-busy');
    try {
        const data = new FormData(form);
        if (form.id === 'signin-form') {
            const server = String(data.get('server') || settings().sync.serverUrl).trim();
            state.app = await call('signIn', { server, email: String(data.get('email')).trim(), password: data.get('password'), device: String(data.get('device')).trim() });
            toast('Signed in. Syncing your snippets…');
        } else if (form.id === 'master-form') {
            if (data.get('password') !== data.get('repeat')) throw new Error('The two passwords do not match.');
            const protectedCount = await call('setMasterPassword', { password: data.get('password'), protectPasswordFolders: data.get('protectFolders') === 'on' });
            toast(protectedCount ? `Master password set. ${plural(protectedCount, 'snippet')} protected.` : 'Master password set');
        }

        await refreshState();
        renderPage();
        renderChrome();
    } catch (failure) {
        error.textContent = failure.message;
        error.hidden = false;
        submit.disabled = false;
        submit.classList.remove('is-busy');
    }
});

document.addEventListener('keydown', async (event) => {
    const ctrl = event.ctrlKey || event.metaKey;
    const key = event.key.toLowerCase();

    if (event.target.matches?.('.tags input')) {
        if (event.key === 'Enter' || event.key === ',' || event.key === '|') {
            event.preventDefault();
            addTag(event.target);
            return;
        }

        if (event.key === 'Backspace' && !event.target.value && state.draft.abbreviations.length) {
            state.draft.abbreviations.pop();
            renderEditor();
            $('#editor .tags input').focus();
            return;
        }
    }

    if (ctrl && key === 's' && state.view === 'snippets') { event.preventDefault(); return save(); }
    if (ctrl && key === 'n') { event.preventDefault(); return newSnippet(); }
    if (ctrl && key === 'f') {
        event.preventDefault();
        showView('snippets');
        $('#search').focus();
        $('#search').select();
        return;
    }

    // Arrow keys move between options in segmented controls and theme cards, like radio buttons.
    const option = event.target.closest?.('.segmented button, .theme-card');
    if (option && ['ArrowLeft', 'ArrowRight', 'ArrowUp', 'ArrowDown'].includes(event.key)) {
        const group = option.matches('.theme-card') ? $$('.theme-card') : $$('button', option.parentElement);
        const step = event.key === 'ArrowRight' || event.key === 'ArrowDown' ? 1 : group.length - 1;
        const next = group[(group.indexOf(option) + step) % group.length];
        next.click();
        next.focus();
        event.preventDefault();
        return;
    }

    // Up and down walk the snippet list from the search box or the list itself.
    if ((event.target.id === 'search' || event.target.closest?.('#list')) && (event.key === 'ArrowDown' || event.key === 'ArrowUp')) {
        event.preventDefault();
        if (state.visible.length === 0) return;
        const index = state.visible.findIndex((s) => s.id === state.selectedId);
        const next = state.visible[Math.max(0, Math.min(state.visible.length - 1, index + (event.key === 'ArrowDown' ? 1 : -1)))];
        await select(next.id);
        scrollToSelected();
        renderList();
    }
});

$('#list').addEventListener('scroll', () => requestAnimationFrame(renderList));
window.addEventListener('resize', () => requestAnimationFrame(renderList));

// Title bar: drag to move, double-click to maximize, chips act on their state.
$('#titlebar').addEventListener('mousedown', (event) => {
    if (event.button === 0 && !event.target.closest('button')) {
        call('window', { action: 'drag' });
    }
});
// Edge handles: Windows takes the resize over from the first mousedown.
document.querySelector('.resize-edges').addEventListener('mousedown', (event) => {
    const edge = event.target.closest('[data-edge]')?.dataset.edge;
    if (event.button === 0 && edge) {
        event.preventDefault();
        call('window', { action: `resize:${edge}` });
    }
});
on('window', ({ maximized }) => document.body.classList.toggle('is-maximized', maximized));
$('#titlebar').addEventListener('dblclick', (event) => {
    if (!event.target.closest('button')) call('window', { action: 'maximize' });
});
$('#chip-expansion').addEventListener('click', () => saveSetting('expansionEnabled', !settings().expansionEnabled));
$('#chip-sync').addEventListener('click', () => (state.app.sync.signedIn ? call('syncNow') : showView('account')));
$('#chip-vault').addEventListener('click', async () => {
    if (state.app.vault.unlocked) await call('lock');
    else await unlockDialog();
});

// ---- Live updates from the host ------------------------------------------------------

on('change', async (kind) => {
    await refreshState();
    if (kind === 'snippets') {
        await refreshSnippets();
        if (state.draft?.id && !state.snippets.some((s) => s.id === state.draft.id)) {
            state.draft = null;
            state.selectedId = null;
        }

        renderFolders();
        renderList();
        if (!isDirty()) {
            const current = state.snippets.find((s) => s.id === state.selectedId);
            if (current) {
                state.draft = { ...structuredClone(current), expansion: current.protected ? state.draft?.expansion ?? null : current.expansion };
                state.original = JSON.stringify(state.draft);
            }

            renderEditor();
        }
    }

    if (kind === 'vault' && state.draft?.protected) {
        state.draft.expansion = state.app.vault.unlocked && state.draft.id ? await call('revealSnippet', { id: state.draft.id }).catch(() => null) : null;
        state.original = JSON.stringify(state.draft);
        renderEditor();
    }

    if (kind === 'settings') applyTheme(settings().theme, false);
    renderChrome();
    if (kind !== 'stats' && state.view !== 'snippets') renderPage();
});

on('notice', ({ message, error }) => toast(message, error));

// ---- Start ------------------------------------------------------------------------------

await Promise.all([refreshState(), refreshSnippets()]);
applyTheme(settings().theme, false);
renderAllSnippetParts();
filterSnippets();
if (state.visible.length > 0) {
    await select(state.visible[0].id);
}

setInterval(renderChrome, 30000);
