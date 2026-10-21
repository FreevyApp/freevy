// Freevy desktop UI: renders the app state from C# (or the demo backend) and sends user actions back.
import { call, on, isDemo } from './bridge.js';
import { $, $$, escapeHtml, icon, number, timeAgo, dialog, confirmDialog, menu, toast } from './ui.js';
import { t, setLanguage, translateStatic } from './i18n.js';

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
// A sidebar filter, not a folder: snippets whose abbreviation another snippet also uses.
const DUPLICATES = '__duplicates__';
const ROW_HEIGHT = 56;
const folderColors = ['oklch(0.84 0.13 182)', 'oklch(0.78 0.12 290)', 'oklch(0.80 0.12 230)', 'oklch(0.85 0.12 85)', 'oklch(0.78 0.14 350)', 'oklch(0.82 0.13 150)', 'oklch(0.80 0.12 40)'];
const colorFor = (name) => folderColors[[...name].reduce((sum, c) => sum + c.charCodeAt(0), 0) % folderColors.length];

const state = {
    editMode: false, // the list shows checkboxes for bulk actions
    checked: new Set(), // ids ticked in edit mode
    app: null,
    browserSignIn: null, // { fingerprint } while the website finishes a browser sign-in
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
        toast(t(failure.message), true);
        return undefined;
    }
}

const settings = () => state.app.settings;
const isMasked = (s) => settings().maskFoldersContaining && s.folder.toLowerCase().includes(settings().maskFoldersContaining.toLowerCase());
const firstLine = (text) => (text ?? '').split(String.fromCharCode(10))[0];
// The words are translation keys: "snippet" and "snippets" each have an entry per language.
const plural = (n, word, many = `${word}s`) => `${number.format(n)} ${n === 1 ? t(word) : t(many)}`;

async function refreshState() {
    state.app = await call('getState');
}

async function refreshSnippets() {
    state.snippets = await call('getSnippets');
    state.snippets.sort((a, b) => a.abbreviations[0].localeCompare(b.abbreviations[0], undefined, { sensitivity: 'base' }));
}

function unlockDialog(reason = t('Enter your master password to use protected snippets.')) {
    return dialog({
        title: t('Unlock protected snippets'),
        body: escapeHtml(reason),
        fields: [{ label: t('Master password'), type: 'password', autocomplete: 'current-password' }],
        confirm: t('Unlock'),
        onSubmit: async ({ fields }) => {
            if (!(await call('unlock', { password: fields[0] }))) {
                throw new Error(t('Wrong master password.'));
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
    const version = $('#app-version');
    const update = app.update?.ready ?? app.update?.available;
    const shown = String(app.version ?? '').split('+')[0];
    version.textContent = /^\d/.test(shown) ? `v${shown}` : shown;
    version.classList.toggle('has-update', Boolean(update));
    version.title = update ? t('Freevy {version} is available', { version: update }) : t('Version and updates');
    const expansion = $('#chip-expansion');
    expansion.className = `chip ${app.settings.expansionEnabled ? 'chip--accent' : 'chip--off'}`;
    $('span:last-child', expansion).textContent = app.settings.expansionEnabled ? t('Expanding') : t('Paused');

    const sync = $('#chip-sync');
    $('span', sync).textContent = !app.sync.signedIn ? t('Not syncing')
        : app.sync.running ? t('Syncing…')
        : app.sync.error ? t('Sync problem')
        : app.sync.lastSync ? t('Synced {time}', { time: timeAgo(app.sync.lastSync) }) : t('Sync pending');
    sync.title = app.sync.error ?? (app.sync.signedIn ? t('Sync now') : t('Sign in to sync'));
    sync.classList.toggle('chip--warning', Boolean(app.sync.error));
    sync.classList.toggle('is-busy', app.sync.running);

    const vault = $('#chip-vault');
    vault.hidden = !app.vault.configured;
    vault.className = `chip ${app.vault.unlocked ? 'chip--accent' : 'chip--warning'}`;
    $('use', vault).setAttribute('href', app.vault.unlocked ? '#i-unlock' : '#i-lock');
    $('span', vault).textContent = app.vault.unlocked ? t('Unlocked until {time}', { time: app.vault.locksAt }) : t('Vault locked');

    $('#nav-count').textContent = number.format(state.snippets.length);
    $('#saved-today').textContent = plural(app.stats.today.keystrokesSaved, 'keystroke');
    $('#saved-detail').textContent = t('{expansions} today · {total} in total', { expansions: plural(app.stats.today.expansions, 'expansion'), total: number.format(app.stats.totalKeystrokesSaved) });
}

function renderFolders() {
    const counts = new Map();
    state.snippets.forEach((s) => counts.set(s.folder, (counts.get(s.folder) ?? 0) + 1));
    const folders = [...counts.keys()].filter(Boolean).sort((a, b) => a.localeCompare(b));
    const selected = state.folder === null ? ALL : state.folder;
    const row = (value, label, count, locked, withMenu) => `
        <div class="folder-row">
            <button class="folder-item${value === DUPLICATES ? ' folder-item--warning' : ''}" data-folder="${escapeHtml(value)}" aria-pressed="${selected === value}">
                ${value === DUPLICATES ? icon('alert') : `<span class="dot" data-color="${value === ALL ? '' : colorFor(value || '?')}"></span>`}
                <span class="name">${escapeHtml(label)}</span>
                <span class="count">${locked ? icon('lock') : ''}${number.format(count)}</span>
            </button>
            ${withMenu ? `<button class="folder-menu-button" data-folder-menu="${escapeHtml(value)}" aria-label="${escapeHtml(t('Actions for {name}', { name: label }))}" aria-haspopup="menu" aria-expanded="false">${icon('more')}</button>` : ''}
        </div>`;
    const duplicateCount = duplicates().size;
    if (state.folder === DUPLICATES && duplicateCount === 0) state.folder = null;
    $('#folders').innerHTML = row(ALL, t('All snippets'), state.snippets.length, false, false)
        + (duplicateCount ? row(DUPLICATES, t('Duplicates'), duplicateCount, false, false) : '')
        + (counts.get('') ? row('', t('No folder'), counts.get(''), false, false) : '')
        + folders.map((f) => row(f, f, counts.get(f), state.snippets.some((s) => s.folder === f && s.protected), true)).join('');
    // Set through the DOM: the page's CSP forbids inline style attributes.
    $$('.folder-item .dot[data-color]').forEach((dot) => {
        if (dot.dataset.color) {
            dot.style.setProperty('--folder-color', dot.dataset.color);
        }
    });
}

function folderMenu(trigger, folder) {
    const inFolder = state.snippets.filter((s) => s.folder === folder || s.folder.startsWith(folder + '/'));
    const count = inFolder.length;
    const locked = inFolder.filter((s) => s.protected).length;
    const open = count - locked;
    // Only the actions that would change something; a mixed folder gets both, with counts.
    const protection = [];
    if (open > 0) {
        protection.push({ label: locked > 0 ? t('Protect {count} more', { count: number.format(open) }) : t('Protect all snippets'), icon: 'lock', action: () => protectFolder(folder, true) });
    }
    if (locked > 0) {
        protection.push({ label: open > 0 ? t('Remove protection ({count})', { count: number.format(locked) }) : t('Remove protection'), icon: 'unlock', action: () => protectFolder(folder, false) });
    }
    menu(trigger, [
        { label: t('Rename folder…'), icon: 'pencil', action: () => renameFolder(folder) },
        ...protection,
        'separator',
        { label: t('Delete folder ({count})…', { count }), icon: 'trash', danger: true, action: () => deleteFolder(folder, count) },
    ]);
}

async function renameFolder(folder) {
    const result = await dialog({
        title: t('Rename folder'),
        body: t('Use / for subfolders, for example Games/WoW.'),
        fields: [{ label: t('Folder name'), value: folder }],
        confirm: t('Rename'),
        onSubmit: ({ fields }) => call('renameFolder', { folder, name: fields[0] }),
    });
    if (result) {
        if (state.folder === folder) state.folder = result.fields[0].trim().replace(/^\/+|\/+$/g, '');
        toast(t('Folder renamed'));
    }
}

async function protectFolder(folder, protect) {
    if (!state.app.vault.configured) {
        toast(t('Set a master password first, on the Master password page.'), true);
        return;
    }

    if (!state.app.vault.unlocked && !(await unlockDialog())) {
        return;
    }

    await run(() => call('protectFolder', { folder, protect }), (n) => t(protect ? '{count} protected' : '{count} unprotected', { count: plural(n, 'snippet') }));
}

async function deleteFolder(folder, count) {
    if (await confirmDialog({ title: t('Delete “{name}”?', { name: folder }), body: t('This deletes the folder and its {count}. Backups keep older copies.', { count: plural(count, 'snippet') }), confirm: t('Delete folder'), danger: true })) {
        if (state.folder === folder) state.folder = null;
        await run(() => call('deleteFolder', { folder }), t('Folder deleted'));
    }
}

// ---- Folder picker ------------------------------------------------------------------------

/** Options for the folder field: existing folders matching the text, plus "create" for a new name. */
function folderOptions(text) {
    const counts = new Map();
    state.snippets.forEach((s) => s.folder && counts.set(s.folder, (counts.get(s.folder) ?? 0) + 1));
    const query = text.trim().replace(/^\/+|\/+$/g, '');
    const lower = query.toLowerCase();
    const options = [...counts.keys()]
        .filter((f) => !lower || f.toLowerCase().includes(lower))
        // Names that start with the text first, then the rest alphabetically.
        .sort((a, b) => (b.toLowerCase().startsWith(lower) - a.toLowerCase().startsWith(lower)) || a.localeCompare(b))
        .map((f) => ({ value: f, label: f, count: counts.get(f) }));
    if (!query) options.unshift({ value: '', label: t('No folder'), none: true });
    else if (![...counts.keys()].some((f) => f.toLowerCase() === lower)) options.push({ value: query, label: t('Create folder “{name}”', { name: query }), create: true });
    return options;
}

function renderCombo(input) {
    const list = input.parentElement.querySelector('.combo-list');
    // Until the user types, list every folder (the current one highlighted) instead of filtering by it.
    const typed = input.dataset.typed === 'true';
    const options = folderOptions(typed ? input.value : '');
    if (!typed && input.dataset.active === undefined) {
        input.dataset.active = String(Math.max(0, options.findIndex((o) => o.value === input.value.trim())));
    }
    const active = Math.min(Number(input.dataset.active ?? 0), options.length - 1);
    input.dataset.active = String(Math.max(0, active));
    list.innerHTML = options.map((o, i) => `
        <div class="combo-option${o.create ? ' combo-option--create' : ''}" role="option" id="folder-option-${i}" data-value="${escapeHtml(o.value)}" aria-selected="${i === active}">
            ${o.create ? icon('plus') : o.none ? '<span class="dot"></span>' : `<span class="dot" data-color="${colorFor(o.value)}"></span>`}
            <span class="combo-label">${escapeHtml(o.label)}</span>
            ${o.count ? `<span class="combo-count">${number.format(o.count)}</span>` : ''}
        </div>`).join('');
    $$('.combo-option .dot[data-color]', list).forEach((dot) => dot.style.setProperty('--folder-color', dot.dataset.color));
    input.setAttribute('aria-activedescendant', options.length ? `folder-option-${active}` : '');
    list.querySelector('[aria-selected="true"]')?.scrollIntoView({ block: 'nearest' });
}

function openCombo(input) {
    const list = input.parentElement.querySelector('.combo-list');
    if (list.hidden) {
        delete input.dataset.active;
        input.dataset.typed = 'false';
    }
    list.hidden = false;
    input.setAttribute('aria-expanded', 'true');
    renderCombo(input);
}

function closeCombo(input) {
    const list = input.parentElement?.querySelector('.combo-list');
    if (!list) return;
    list.hidden = true;
    input.setAttribute('aria-expanded', 'false');
}

function pickCombo(input, value) {
    input.value = value;
    input.dataset.picking = 'true';
    input.dispatchEvent(new Event('input', { bubbles: true }));
    delete input.dataset.picking;
    closeCombo(input);
}

document.addEventListener('focusin', (event) => {
    if (event.target.matches?.('[data-combo]')) openCombo(event.target);
});
document.addEventListener('focusout', (event) => {
    if (event.target.matches?.('[data-combo]')) closeCombo(event.target);
});
document.addEventListener('mousedown', (event) => {
    const option = event.target.closest?.('.combo-option');
    if (!option) return;
    // Keep focus in the field, so the click lands before the list closes.
    event.preventDefault();
    pickCombo(option.closest('.combo').querySelector('[data-combo]'), option.dataset.value);
});
document.addEventListener('keydown', (event) => {
    const input = event.target;
    if (!input.matches?.('[data-combo]')) return;
    const list = input.parentElement.querySelector('.combo-list');
    const count = list.querySelectorAll('.combo-option').length;
    if (event.key === 'ArrowDown' || event.key === 'ArrowUp') {
        event.preventDefault();
        if (list.hidden) return openCombo(input);
        const step = event.key === 'ArrowDown' ? 1 : -1;
        input.dataset.active = String((Number(input.dataset.active ?? 0) + step + count) % count);
        renderCombo(input);
    } else if (event.key === 'Enter' && !list.hidden && count) {
        event.preventDefault();
        pickCombo(input, list.querySelector('[aria-selected="true"]').dataset.value);
    } else if (event.key === 'Escape' && !list.hidden) {
        event.preventDefault();
        event.stopPropagation();
        closeCombo(input);
    }
}, true);
document.addEventListener('input', (event) => {
    if (event.target.matches?.('[data-combo]') && document.activeElement === event.target && !event.target.dataset.picking) {
        event.target.dataset.active = '0';
        event.target.dataset.typed = 'true';
        const list = event.target.parentElement.querySelector('.combo-list');
        list.hidden = false;
        event.target.setAttribute('aria-expanded', 'true');
        renderCombo(event.target);
    }
});

// ---- Duplicates -----------------------------------------------------------------------

let duplicateCache = { source: null, map: new Map() };

/** id -> abbreviations of that snippet which another snippet also uses (case is ignored). */
function duplicates() {
    if (duplicateCache.source === state.snippets) return duplicateCache.map;
    const owners = new Map();
    for (const s of state.snippets) {
        for (const a of new Set(s.abbreviations.map((x) => x.toLowerCase()))) {
            if (!owners.has(a)) owners.set(a, []);
            owners.get(a).push(s.id);
        }
    }
    const map = new Map();
    for (const [abbr, ids] of owners) {
        if (ids.length < 2) continue;
        for (const id of ids) {
            if (!map.has(id)) map.set(id, []);
            map.get(id).push(abbr);
        }
    }
    duplicateCache = { source: state.snippets, map };
    return map;
}

function duplicateNote(s) {
    const shared = duplicates().get(s.id);
    if (!shared) return '';
    const others = new Set(state.snippets.filter((x) => x.id !== s.id && x.abbreviations.some((a) => shared.includes(a.toLowerCase()))).map((x) => x.id)).size;
    return t('{abbreviations} is also used by {count}. Only one of them can expand.', { abbreviations: shared.map((a) => `“${a}”`).join(', '), count: plural(others, 'other snippet') });
}

/**
 * In the Duplicates view: every exact copy (same abbreviations and text) except the one to keep
 * in each group, which is the most complete: it has a description, then the longer one, then the oldest.
 */
function extraCopies() {
    const groups = new Map();
    for (const s of state.visible) {
        if (s.protected || s.expansion == null) continue;
        const key = JSON.stringify([[...s.abbreviations].map((a) => a.toLowerCase()).sort(), s.expansion]);
        if (!groups.has(key)) groups.set(key, []);
        groups.get(key).push(s);
    }
    const score = (s) => [s.description ? 1 : 0, (s.description ?? '').length, -(Date.parse(s.created ?? s.modified ?? '') || 0)];
    const better = (a, b) => {
        const [x, y] = [score(a), score(b)];
        for (let i = 0; i < x.length; i++) if (x[i] !== y[i]) return x[i] > y[i] ? a : b;
        return a;
    };
    const extra = [];
    for (const copies of groups.values()) {
        if (copies.length < 2) continue;
        const keep = copies.reduce(better);
        copies.forEach((s) => s !== keep && extra.push(s.id));
    }
    return extra;
}

// ---- Snippet list (virtualized) -----------------------------------------------------

function filterSnippets() {
    const query = state.query.trim().toLowerCase();
    const dupes = duplicates();
    const inView = (s) => state.folder === DUPLICATES ? dupes.has(s.id)
        : state.folder === null || s.folder === state.folder || (state.folder !== '' && s.folder.startsWith(state.folder + '/'));
    state.visible = state.snippets.filter((s) =>
        inView(s)
        && (!query
            || s.abbreviations.some((a) => a.toLowerCase().includes(query))
            || s.description.toLowerCase().includes(query)
            || (!s.protected && !isMasked(s) && (s.expansion ?? '').toLowerCase().includes(query))));
    if (state.folder === DUPLICATES) {
        // Copies next to each other: by the shared abbreviation, then by folder.
        const key = (s) => [...dupes.get(s.id)].sort()[0];
        state.visible.sort((a, b) => key(a).localeCompare(key(b)) || a.folder.localeCompare(b.folder));
    }
}

function renderList() {
    filterSnippets();
    const list = $('#list');
    const spacer = $('#list-spacer');
    renderListFooter();

    if (state.visible.length === 0) {
        spacer.style.height = 'auto';
        spacer.innerHTML = state.snippets.length === 0
            ? `<div class="empty">${icon('keyboard')}<strong>${t('No snippets yet')}</strong><span>${t('Create your first one, or import your Breevy library in Settings.')}</span></div>`
            : `<div class="empty">${icon('search')}<strong>${state.query ? escapeHtml(t('Nothing matches “{query}”', { query: state.query })) : t('Nothing matches here')}</strong><span>${t('Try another word or folder, or create a new snippet.')}</span></div>`;
        return;
    }

    spacer.style.height = `${state.visible.length * ROW_HEIGHT}px`;
    const first = Math.max(0, Math.floor(list.scrollTop / ROW_HEIGHT) - 6);
    const last = Math.min(state.visible.length, first + Math.ceil(list.clientHeight / ROW_HEIGHT) + 12);
    let html = '';
    for (let i = first; i < last; i++) {
        const s = state.visible[i];
        const primary = s.protected ? `<span class="locked">${icon('lock')}${t('Protected')}</span>`
            : isMasked(s) ? `<span class="masked">${t('Hidden')}</span>`
            : escapeHtml((s.expansion ?? '').split('\n')[0] || t('(empty)'));
        const checked = state.editMode && state.checked.has(s.id);
        const warning = duplicateNote(s);
        html += `
            <button class="snippet-row${state.editMode ? ' snippet-row--editing' : ''}" role="option" data-id="${s.id}" aria-selected="${state.editMode ? checked : s.id === state.selectedId}" tabindex="-1" data-top="${i * ROW_HEIGHT}">
                ${state.editMode ? `<span class="row-check" data-checked="${checked}" aria-hidden="true">${icon('check')}</span>` : ''}
                <span class="abbr">${escapeHtml(s.abbreviations[0])}</span>
                ${warning ? `<span class="dup-warn" title="${escapeHtml(warning)}" aria-label="${escapeHtml(warning)}">${icon('alert')}</span>` : ''}
                <span class="snippet-text">
                    <span class="primary">${primary}</span>
                    <span class="secondary">${escapeHtml(s.description || s.folder || t('No folder'))}${s.abbreviations.length > 1 ? ` · +${s.abbreviations.length - 1}` : ''}</span>
                </span>
                ${state.editMode ? `<span class="row-open${s.id === state.selectedId ? ' is-open' : ''}" title="${t('Open this snippet')}">${icon('eye')}</span>` : ''}
            </button>`;
    }

    spacer.innerHTML = html;
    $$('.snippet-row', spacer).forEach((row) => { row.style.top = `${row.dataset.top}px`; });
}

function renderListFooter() {
    const footer = $('#list-footer');
    if (!state.editMode) {
        footer.innerHTML = `<span>${plural(state.visible.length, 'snippet')}</span>
            <span class="footer-actions">
                <button class="btn btn--primary btn--small" id="new-snippet">${icon('plus')}${t('New snippet')}</button>
            </span>`;
        return;
    }

    const count = state.checked.size;
    const allVisible = state.visible.length > 0 && state.visible.every((s) => state.checked.has(s.id));
    footer.innerHTML = `<span>${count ? t('{count} selected', { count: number.format(count) }) : t('Select snippets')}</span>
        <span class="footer-actions">
            ${state.folder === DUPLICATES ? `<button class="btn btn--ghost btn--small" data-action="select-extra">${t('Select extra copies')}</button>` : ''}
            <button class="btn btn--ghost btn--small" data-action="select-all">${allVisible ? t('Select none') : t('Select all')}</button>
            <button class="btn btn--small" data-action="move-checked" ${count ? '' : 'disabled'}>${icon('folder')}${t('Move…')}</button>
            <button class="btn btn--small btn--danger-solid" data-action="delete-checked" ${count ? '' : 'disabled'}>${icon('trash')}${t('Delete')}${count ? ` (${number.format(count)})` : ''}</button>
            <button class="btn btn--small" data-action="edit-done">${t('Done')}</button>
        </span>`;
}

function setEditMode(on) {
    state.editMode = on;
    $('#edit-toggle').setAttribute('aria-pressed', String(on));
    state.checked.clear();
    state.anchorId = null;
    renderList();
}

function toggleChecked(id, keepOpen = false) {
    if (!state.editMode) {
        state.editMode = true;
        $('#edit-toggle').setAttribute('aria-pressed', 'true');
        state.checked.clear();
        // The open snippet joins the selection, like Outlook keeps the current message.
        if (keepOpen && state.selectedId && state.selectedId !== id) state.checked.add(state.selectedId);
    }
    if (state.checked.has(id)) state.checked.delete(id);
    else state.checked.add(id);
    state.anchorId = id;
    renderList();
}

function selectRange(id) {
    const anchor = state.anchorId ?? state.selectedId;
    const from = state.visible.findIndex((s) => s.id === anchor);
    const to = state.visible.findIndex((s) => s.id === id);
    if (!state.editMode) {
        state.editMode = true;
        $('#edit-toggle').setAttribute('aria-pressed', 'true');
        state.checked.clear();
    }
    if (from < 0 || to < 0) return toggleChecked(id);
    const [start, end] = from < to ? [from, to] : [to, from];
    state.visible.slice(start, end + 1).forEach((s) => state.checked.add(s.id));
    renderList();
}

async function importFrom(kind, label) {
    const result = await run(() => call('importFrom', { kind }));
    if (!result) return;
    const parts = [t('Added {count} from {source}', { count: plural(result.added, 'snippet'), source: label })];
    if (result.duplicates) parts.push(t('{count} already here', { count: number.format(result.duplicates) }));
    if (result.skipped) parts.push(t('{count} skipped (regex, images or forms)', { count: number.format(result.skipped) }));
    if (result.adjusted) parts.push(t('{count} with placeholders to check', { count: number.format(result.adjusted) }));
    toast(parts.join(' · '));
}

async function moveChecked() {
    const ids = [...state.checked];
    if (!ids.length) return;
    const folders = [...new Set(state.snippets.map((s) => s.folder).filter(Boolean))].sort((a, b) => a.localeCompare(b));
    const result = await dialog({
        title: t('Move {count}', { count: plural(ids.length, 'snippet') }),
        body: t('Pick a folder or type a new name. Leave it empty for no folder.'),
        fields: [{ label: t('Folder'), suggestions: folders, required: false, placeholder: t('Folder name') }],
        confirm: t('Move'),
    });
    if (!result) return;
    const folder = result.fields[0].trim().replace(/^\/+|\/+$/g, '');
    const moved = await run(() => call('moveSnippets', { ids, folder }));
    if (moved === undefined) return;
    toast(moved ? t('Moved {count} to {folder}', { count: plural(moved, 'snippet'), folder: folder || t('No folder') }) : t('They were already there'));
    setEditMode(false);
}

async function deleteChecked() {
    const ids = [...state.checked];
    if (!ids.length) return;
    const locked = state.snippets.filter((s) => ids.includes(s.id) && s.protected).length;
    const body = `${locked ? `${t('{count} included.', { count: plural(locked, 'protected snippet') })} ` : ''}${t('Backups keep older copies.')}`;
    if (!(await confirmDialog({ title: t('Delete {count}?', { count: plural(ids.length, 'snippet') }), body, confirm: t('Delete'), danger: true }))) return;
    if (ids.includes(state.selectedId)) {
        state.selectedId = null;
        state.draft = null;
        state.original = null;
    }
    const done = await run(() => call('deleteSnippets', { ids }), t('{count} deleted', { count: plural(ids.length, 'snippet') }));
    if (done !== undefined) {
        state.checked.clear();
        await refreshSnippets();
        renderFolders();
        renderList();
        renderEditor();
    }
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
    return confirmDialog({ title: t('Discard changes?'), body: t('This snippet has unsaved changes.'), confirm: t('Discard'), cancel: t('Keep editing'), danger: true });
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
    state.draft = { id: null, abbreviations: [], expansion: '', description: '', folder: state.folder === DUPLICATES ? '' : state.folder ?? '', trigger: s.defaultTrigger, wordStart: s.defaultWordStart, case: s.defaultCase, lineBreaks: 'default', protected: false };
    state.original = JSON.stringify(state.draft);
    showView('snippets');
    renderEditor();
    $('#editor .tags input')?.focus();
}

// Per snippet; "default" follows Settings > Typing > Line breaks.
const LINE_BREAK_OPTIONS = [['default', 'Like in Settings'], ['paste', 'Paste the text'], ['shiftEnter', 'Type Shift+Enter'], ['enter', 'Type Enter']];

// "Like in Settings (Paste the text)": names what the setting currently is.
const defaultLineBreakLabel = () => t('Like in Settings ({mode})', { mode: t(LINE_BREAK_OPTIONS.find(([v]) => v === settings().lineBreaks)?.[1] ?? 'Paste the text') });

function segmented(name, key, value, options) {
    return `<div class="segmented" role="radiogroup" aria-label="${name}" data-segment="${key}">${options.map(([v, label]) =>
        `<button type="button" role="radio" data-value="${v}" aria-checked="${v === value}" tabindex="${v === value ? 0 : -1}">${label}</button>`).join('')}</div>`;
}

function renderEditor() {
    const d = state.draft;
    const editor = $('#editor');
    if (!d) {
        renderSaveBar();
        editor.innerHTML = `<div class="empty">${icon('keyboard')}<strong>${t('Pick a snippet')}</strong><span>${t('Or create a new one with {key}.', { key: '<kbd>Ctrl N</kbd>' })}</span></div>`;
        return;
    }

    const folders = [...new Set(state.snippets.map((s) => s.folder).filter(Boolean))].sort();
    const locked = d.protected && d.expansion === null;
    editor.innerHTML = `
        <div class="editor-card">
            <div class="editor-head">
                <div><div class="eyebrow">${escapeHtml(d.folder || t('No folder'))}</div><h1 class="editor-title">${escapeHtml(d.abbreviations[0] ?? t('New snippet'))}</h1></div>
                <div class="editor-actions">
                    ${d.id ? `<button class="btn btn--ghost" data-action="duplicate">${icon('copy')}${t('Duplicate')}</button><button class="btn btn--ghost btn--danger" data-action="delete">${icon('trash')}${t('Delete')}</button>` : ''}
                </div>
            </div>
            <div class="fields">
                <label class="field"><span>${t('Abbreviations')}</span>
                    <div class="tags">${d.abbreviations.map((a, i) => `<span class="tag">${escapeHtml(a)}<button type="button" data-remove-tag="${i}" aria-label="${escapeHtml(t('Remove {name}', { name: a }))}">${icon('x')}</button></span>`).join('')}<input data-field="tag" placeholder="${d.abbreviations.length ? t('Add…') : t('Type an abbreviation, press Enter')}" aria-label="${t('Add abbreviation')}" spellcheck="false"></div>
                </label>
                <label class="field"><span>${t('Description')}</span><input class="input" data-field="description" value="${escapeHtml(d.description)}" placeholder="${t('What is this for?')}"></label>
                <div class="field field--wide"><div class="field-head"><span>${t('Expansion')}</span>${locked ? '' : `<button type="button" class="btn btn--ghost btn--small" data-action="insert-macro" aria-haspopup="menu" aria-expanded="false">${icon('plus')}${t('Insert')}</button>`}</div>
                    ${locked
                        ? `<div class="empty">${icon('lock')}<strong>${t('Protected snippet')}</strong><span>${t('Unlock with your master password to view or edit it.')}</span><button type="button" class="btn btn--primary btn--small" data-action="unlock">${t('Unlock')}</button></div>`
                        : `<textarea class="textarea" data-field="expansion" spellcheck="false" placeholder="${t('The text Freevy types for you')}">${escapeHtml(d.expansion ?? '')}</textarea>`}
                </div>
                <div class="field"><span id="folder-label">${t('Folder')}</span>
                    <div class="combo">
                        <input class="input" data-field="folder" data-combo="folder" role="combobox" aria-labelledby="folder-label" aria-expanded="false" aria-autocomplete="list" aria-controls="folder-options" autocomplete="off" spellcheck="false" value="${escapeHtml(d.folder)}" placeholder="${t('No folder')}">
                        <span class="combo-caret" aria-hidden="true">${icon('folder')}</span>
                        <div class="combo-list" id="folder-options" role="listbox" hidden></div>
                    </div>
                </div>
            </div>
            <div class="options">
                <div class="option-card"><span class="label">${t('Expand')}</span>${segmented(t('Expand'), 'trigger', d.trigger, [['after', t('After space')], ['instant', t('Instantly')]])}
                    <label class="check"><input type="checkbox" data-field="wordStart" ${d.wordStart ? 'checked' : ''}><span>${t('Only at the start of a word')}</span></label></div>
                <div class="option-card"><span class="label">${t('Case')}</span>${segmented(t('Case'), 'case', d.case, [['exact', t('Exact')], ['ignore', t('Ignore')], ['adapt', t('Adapt')]])}</div>
                <div class="option-card"><span class="label">${t('Line breaks')}</span>
                    <select class="input" data-field="lineBreaks" aria-label="${t('Line breaks')}">${LINE_BREAK_OPTIONS.map(([v, label]) => `<option value="${v}" ${v === (d.lineBreaks ?? 'default') ? 'selected' : ''}>${v === 'default' ? defaultLineBreakLabel() : t(label)}</option>`).join('')}</select></div>
                <div class="option-card option-card--row"><div><div class="label">${t('Protected')}</div><div class="hint">${t('Needs master password')}</div></div><button type="button" class="switch" role="switch" data-field="protected" aria-checked="${d.protected}" aria-label="${t('Protected')}"></button></div>
            </div>
            <div class="preview"><div class="label">${t('Preview')}</div><p id="preview-line"></p><div class="macro-preview" id="macro-preview" hidden></div></div>
        </div>`;
    updateEditorChrome();
}

// Shown only while the draft differs from what is saved; lives outside #editor so re-rendering
// the editor never replays its entrance.
function renderSaveBar() {
    const dirty = isDirty();
    $('#save-bar').hidden = !dirty;
    $('#editor-pane').classList.toggle('is-dirty', dirty);
}

async function discardChanges() {
    const saved = state.snippets.find((s) => s.id === state.draft?.id);
    if (saved && state.original) {
        state.draft = JSON.parse(state.original);
    } else {
        // A new or duplicated snippet: go back to the snippet that was open before, if any.
        state.draft = null;
        state.original = null;
        const previous = state.snippets.find((s) => s.id === state.selectedId);
        if (previous) {
            state.selectedId = null;
            return select(previous.id);
        }
    }

    renderList();
    renderEditor();
}

function updateEditorChrome() {
    const d = state.draft;
    if (!d) return;
    renderSaveBar();
    const caseNote = d.case === 'adapt' ? ` ${t('Type it in capitals to get capitals.')}` : '';
    $('#preview-line').innerHTML = t('You type {abbreviation} and Freevy types the expansion.', { abbreviation: `<kbd>${escapeHtml(d.abbreviations[0] ?? '…')}${d.trigger === 'after' ? '␣' : ''}</kbd>` }) + caseNote;
    scheduleMacroPreview();
}

// ---- Macros: preview and Insert menu ------------------------------------------------------

let macroPreviewTimer;
function scheduleMacroPreview() {
    clearTimeout(macroPreviewTimer);
    macroPreviewTimer = setTimeout(renderMacroPreview, 120);
}

async function renderMacroPreview() {
    const box = $('#macro-preview');
    const text = state.draft?.expansion ?? '';
    if (!box) return;
    if (!text.includes('%(')) {
        box.hidden = true;
        return;
    }
    const preview = await call('previewExpansion', { text }).catch(() => null);
    if (!preview?.hasMacros || state.draft?.expansion !== text) {
        box.hidden = !preview?.hasMacros;
        return;
    }
    const body = preview.parts.map((p) => p.kind === 'text'
        ? escapeHtml(p.value).replace(/\n/g, '<span class="macro-chip">⏎</span><br>')
        : `<span class="macro-chip">${escapeHtml(p.value)}</span>`).join('');
    const notes = [];
    if (preview.inputs.length) notes.push(t('Asks for {fields} first.', { fields: preview.inputs.map((i) => `“${escapeHtml(i)}”`).join(', ') }));
    if (preview.cursorBack) notes.push(t('Then moves the cursor back {count}.', { count: plural(preview.cursorBack, 'character') }));
    box.innerHTML = `<div class="macro-output">${body || `<span class="hint">${t('(nothing typed)')}</span>`}</div>${notes.length ? `<p class="hint">${notes.join(' ')}</p>` : ''}`;
    box.hidden = false;
}

function insertIntoExpansion(snippet) {
    const area = $('#editor [data-field="expansion"]');
    if (!area) return;
    const start = area.selectionStart ?? area.value.length;
    const end = area.selectionEnd ?? start;
    area.setRangeText(snippet, start, end, 'end');
    area.focus();
    area.dispatchEvent(new Event('input', { bubbles: true }));
}

const DATE_PRESETS = [
    ['%(Day).%(Month).%(Year)', 'Date, day first with dots'],
    ['%(Day)/%(Month)/%(Year)', 'Date, day first'],
    ['%(Month)/%(Day)/%(Year)', 'Date, month first (US)'],
    ['%(Year)-%(Month)-%(Day)', 'Date, ISO (2026-03-05)'],
    ['%(WeekdayName), %(MonthName) %(#Day), %(Year)', 'Long date'],
    ['%(#Day) %(MonthName) %(Year)', 'Day, month name, year'],
    ['%(Hour24):%(Minute)', 'Time, 24-hour'],
    ['%(#Hour):%(Minute) %(AMorPM)', 'Time, 12-hour'],
    ['%(WeekdayName)', 'Weekday name'],
    ['%(MonthName)', 'Month name'],
    ['%(Year)', 'Year'],
    ['%(#WeekOfYear)', 'Week number'],
];

async function insertMacro(trigger) {
    const ask = async (title, fields, body = '') => (await dialog({ title, body, fields, confirm: t('Insert') }))?.fields;
    menu(trigger, [
        { label: t('Line break without sending (Shift+Enter)'), icon: 'keyboard', action: () => insertIntoExpansion('%(Key Shift+Enter)') },
        { label: t('Cursor position'), icon: 'pencil', action: () => insertIntoExpansion('%(cursor)') },
        { label: t('Clipboard text'), icon: 'copy', action: () => insertIntoExpansion('%(clipboard)') },
        'separator',
        { label: t('Fill-in field…'), icon: 'plus', action: async () => {
            const v = await ask(t('Fill-in field'), [{ label: t('Name (fields with the same name are asked once)'), value: '', required: false, placeholder: t('e.g. name') }], t('Freevy asks for this text in a small window before typing the snippet.'));
            if (v) insertIntoExpansion(v[0].trim() ? `%(Input ${v[0].trim()})` : '%(Input)');
        } },
        { label: t('Date or time…'), icon: 'history', action: async () => {
            const v = await ask(t('Date or time'), [{ label: t('Format'), value: DATE_PRESETS[0][0], options: DATE_PRESETS.map(([value, label]) => ({ value, label: `${t(label)}: ${value}` })) }]);
            if (v) insertIntoExpansion(v[0]);
        } },
        { label: t('Shift the date…'), icon: 'refresh', action: async () => {
            const v = await ask(t('Shift the date'), [
                { label: t('Direction'), value: 'Add', options: [{ value: 'Add', label: t('Later (add)') }, { value: 'Subtract', label: t('Earlier (subtract)') }] },
                { label: t('Amount'), value: '1', type: 'number' },
                { label: t('Unit'), value: 'Days', options: ['Days', 'Months', 'Years', 'Hours', 'Minutes', 'Seconds'].map((u) => ({ value: u, label: t(u.toLowerCase()) })) },
            ], t('Applies to the date and time macros after it, for example a due date in 14 days.'));
            if (v) insertIntoExpansion(`%(${v[0]}${v[2]} ${Math.max(0, parseInt(v[1], 10) || 0)})`);
        } },
        'separator',
        { label: t('Key press…'), icon: 'keyboard', action: async () => {
            const keys = ['Enter', 'Tab', 'Escape', 'Backspace', 'Delete', 'Home', 'End', 'PageUp', 'PageDown', 'Up', 'Down', 'Left', 'Right', 'Space', 'Insert', ...Array.from({ length: 12 }, (_, i) => `F${i + 1}`), ...'ABCDEFGHIJKLMNOPQRSTUVWXYZ'.split('')];
            const result = await dialog({
                title: t('Key press'), confirm: t('Insert'),
                fields: [{ label: t('Key'), value: 'Enter', options: keys.map((k) => ({ value: k, label: k })) }],
                checks: [{ label: 'Ctrl' }, { label: 'Shift' }, { label: 'Alt' }, { label: t('Windows key') }],
            });
            if (!result) return;
            const mods = ['Ctrl', 'Shift', 'Alt', 'Win'].filter((_, i) => result.checks[i]);
            insertIntoExpansion(`%(Key ${[...mods, result.fields[0]].join('+')})`);
        } },
        { label: t('Bold, italic or underline…'), icon: 'pencil', action: async () => {
            const v = await ask(t('Formatting'), [{ label: t('Toggle'), value: '%(B)', options: [{ value: '%(B)', label: t('Bold (Ctrl+B)') }, { value: '%(I)', label: t('Italic (Ctrl+I)') }, { value: '%(U)', label: t('Underline (Ctrl+U)') }] }], t('Switches the formatting on; insert it again where it should end.'));
            if (v) insertIntoExpansion(v[0]);
        } },
        { label: t('Wait…'), icon: 'history', action: async () => {
            const v = await ask(t('Wait'), [{ label: t('Milliseconds'), value: '500', type: 'number' }], t('Pauses typing, for apps that need a moment, for example after opening a menu.'));
            if (v) insertIntoExpansion(`%(Delay ${Math.min(60000, Math.max(0, parseInt(v[0], 10) || 0))}ms)`);
        } },
        'separator',
        { label: t('Another snippet…'), icon: 'copy', action: async () => {
            const v = await ask(t('Another snippet'), [{ label: t('Its abbreviation'), value: '', placeholder: t('e.g. ;sig') }], t('Types that snippet’s text here. Protected snippets are left out.'));
            if (v) insertIntoExpansion(`%(Abbrev ${v[0].trim()})`);
        } },
        { label: t('Open a program, file or website…'), icon: 'external', action: async () => {
            const v = await ask(t('Open'), [{ label: t('Program, file, folder or web address'), value: '', placeholder: t('C:\\Tools\\app.exe or https://…') }, { label: t('Arguments (optional)'), value: '', required: false }]);
            if (v) insertIntoExpansion(`%(Launch "${v[0].trim()}"${v[1].trim() ? ` ${v[1].trim()}` : ''})`);
        } },
    ]);
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
        toast(t('Set a master password first, on the Master password page.'), true);
        return;
    }

    const needsKey = (d.protected && d.expansion !== null) || (!d.protected && d.expansion === null);
    if (needsKey && !state.app.vault.unlocked && !(await unlockDialog(t('Saving this snippet needs your master password.')))) return;

    const saved = await run(() => call('saveSnippet', d));
    if (!saved) return;
    state.selectedId = saved.id;
    state.draft = { ...saved, expansion: saved.protected ? (d.expansion ?? null) : saved.expansion };
    state.original = JSON.stringify(state.draft);
    await refreshSnippets();
    renderAllSnippetParts();
    scrollToSelected();
    toast(t('Snippet saved'));
}

async function duplicate() {
    const d = state.draft;
    if (d.protected && d.expansion === null) {
        if (!(await unlockDialog())) return;
        d.expansion = await call('revealSnippet', { id: d.id });
    }

    state.selectedId = null;
    state.draft = { ...structuredClone(d), id: null, description: d.description ? t('{name} (copy)', { name: d.description }) : t('Copy') };
    state.original = '';
    renderList();
    renderEditor();
    toast(t('Duplicated. Change it, then save.'));
}

async function remove() {
    const d = state.draft;
    if (await confirmDialog({ title: t('Delete “{name}”?', { name: d.abbreviations[0] }), body: t('Backups keep older copies.'), confirm: t('Delete'), danger: true })) {
        await run(() => call('deleteSnippets', { ids: [d.id] }), t('Snippet deleted'));
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

// "Backed up to Local folder, Synced folder." comes from Core in English; translate its parts.
function backupResult(text) {
    const done = /^Backed up to (.+)\.$/.exec(text ?? '');
    return done ? t('Backed up to {places}.', { places: done[1].split(', ').map((place) => t(place)).join(', ') }) : t(text ?? '');
}

function renderBackups() {
    const b = settings().backup;
    const sftp = b.sftp;
    const st = state.app.backup;
    const probe = state.sftpProbe;
    const status = st.running ? t('Backing up…') : st.lastRun ? `${t('Last backup {time}.', { time: timeAgo(st.lastRun) })} ${escapeHtml(backupResult(st.result))}` : t('No backup has run yet.');
    const failed = st.lastRun && st.lastRun !== st.lastSuccess;
    $('[data-view="backups"]').innerHTML = `<div class="page-narrow">
        <h1 class="page-title">${t('Backups')}</h1>
        <p class="page-lead">${t('Zip files with your snippets and the encrypted vault. Protected snippets stay encrypted inside them.')}</p>
        <div class="actions">
            <button class="btn btn--primary ${st.running ? 'is-busy' : ''}" data-action="backup-now" ${st.running ? 'disabled' : ''}>${icon('history')}${t('Back up now')}</button>
            <button class="btn" data-action="restore">${icon('upload')}${t('Restore from backup…')}</button>
        </div>
        <p class="inline-note u-mt-3 ${failed ? 'inline-note--error' : ''}">${status}</p>

        ${renderHistoryGroup()}

        <div class="group">
            <div class="group-title">${t('Schedule')}</div>
            ${row(t('Back up automatically'), t('On a timer and after changes, in the background.'), toggle('backup.enabled', b.enabled, t('Back up automatically')))}
            ${row(t('Every'), t('Hours between backups. 0 means only after changes.'), `${numberInput('backup.intervalHours', b.intervalHours, 0, 720)}<span class="inline-note">${t('hours')}</span>`)}
            ${row(t('After changes'), t('A few minutes after you edit snippets.'), toggle('backup.onChange', b.onChange, t('Back up after changes')))}
            ${row(t('Keep'), t('Older backups are deleted in each place.'), `${numberInput('backup.keepCount', b.keepCount, 1, 1000)}<span class="inline-note">${t('newest')}</span>`)}
        </div>

        <div class="group">
            <div class="group-title">${t('This PC')}</div>
            ${row(t('Local folder'), escapeHtml(b.localDirectory), `<button class="btn btn--small" data-choose="backup.localDirectory">${t('Change…')}</button>${toggle('backup.localEnabled', b.localEnabled, t('Local backups'))}`)}
        </div>

        <div class="group">
            <div class="group-title">${t('Synced folder')}</div>
            <div class="group-lead">${t('Google Drive for Desktop, OneDrive or Dropbox upload this folder for you.')}</div>
            ${row(t('Folder'), b.folderDirectory ? escapeHtml(b.folderDirectory) : t('Not chosen yet'), `<button class="btn btn--small" data-choose="backup.folderDirectory">${t('Choose…')}</button>${toggle('backup.folderEnabled', b.folderEnabled, t('Synced folder backups'))}`)}
        </div>

        <div class="group">
            <div class="group-title">${t('SFTP server')}</div>
            <div class="group-lead">${t('Any server you can reach over SSH. The password is stored encrypted for your Windows account.')}</div>
            ${row(t('Back up to this server'), '', toggle('backup.sftp.enabled', sftp.enabled, t('SFTP backups')))}
            <div class="form-grid">
                <label class="field"><span>${t('Host')}</span><input class="input" data-setting="backup.sftp.host" value="${escapeHtml(sftp.host)}" placeholder="backup.example.com" spellcheck="false"></label>
                <label class="field"><span>${t('Port')}</span><input class="input" type="number" data-setting="backup.sftp.port" value="${sftp.port}" min="1" max="65535"></label>
                <label class="field"><span>${t('User')}</span><input class="input" data-setting="backup.sftp.username" value="${escapeHtml(sftp.username)}" spellcheck="false"></label>
                <label class="field"><span>${t('Password')}</span><input class="input" type="password" data-secret="password" placeholder="${sftp.hasPassword ? t('Saved (type to replace)') : t('Optional with a key')}" autocomplete="new-password"></label>
                <label class="field field--wide"><span>${t('Private key')}</span><span class="input-with-button"><input class="input input--mono" data-setting="backup.sftp.privateKeyPath" value="${escapeHtml(sftp.privateKeyPath)}" placeholder="${t('Optional OpenSSH or PuTTY key file')}"><button class="btn btn--small" data-choose-file="backup.sftp.privateKeyPath">${t('Browse…')}</button></span></label>
                <label class="field"><span>${t('Key passphrase')}</span><input class="input" type="password" data-secret="passphrase" placeholder="${sftp.hasPassphrase ? t('Saved (type to replace)') : t('Only if the key has one')}" autocomplete="new-password"></label>
                <label class="field"><span>${t('Remote folder')}</span><input class="input input--mono" data-setting="backup.sftp.remoteDirectory" value="${escapeHtml(sftp.remoteDirectory)}"></label>
            </div>
            <div class="group-actions">
                <button class="btn btn--small" data-action="test-sftp">${icon('server')}${t('Test connection')}</button>
                ${sftp.hostKeyFingerprint
                    ? `<span class="inline-note inline-note--ok">${t('Trusted host key')}<span class="fingerprint">SHA256:${escapeHtml(sftp.hostKeyFingerprint)}</span></span>`
                    : `<span class="inline-note inline-note--warn">${t('Host key not trusted yet. Test the connection to trust it.')}</span>`}
            </div>
            ${probe ? `<div class="group-actions"><span class="inline-note ${probe.changed ? 'inline-note--error' : ''}">${probe.trusted ? t('Connected. The host key matches the trusted one.') : probe.changed ? t('Warning: this server presented a different host key than the trusted one.') : t('Connected. Compare this host key with your server before trusting it:')}<span class="fingerprint">SHA256:${escapeHtml(probe.fingerprint)}</span></span>${probe.trusted ? '' : `<button class="btn btn--small ${probe.changed ? 'btn--danger' : 'btn--primary'}" data-action="trust-key">${t('Trust this key')}</button>`}</div>` : ''}
        </div>
    </div>`;
}

function renderHistoryGroup() {
    if (!state.app.sync.signedIn) {
        return `<div class="group"><div class="group-title">${t('Server history')}</div><div class="group-lead">${t('Sign in on the Sync &amp; account page and the server keeps every earlier version of your snippets for 30 days, encrypted.')}</div><div class="group-actions"></div></div>`;
    }

    const history = state.history;
    let body;
    if (history === null) {
        body = `<div class="group-actions"><button class="btn btn--small" data-action="load-history">${icon('history')}${t('Show server history')}</button></div>`;
    } else if (history === 'loading') {
        body = `<div class="group-actions"><span class="inline-note is-busy">${t('Loading and decrypting on this PC…')}</span></div>`;
    } else if (history.length === 0) {
        body = `<div class="group-actions"><span class="inline-note">${t('No earlier versions in the last 30 days.')}</span></div>`;
    } else {
        body = history.slice(0, 200).map((h) => row(
            `<span class="abbr">${escapeHtml(h.snippet.abbreviations[0])}</span> ${h.snippet.protected ? `<span class="locked">${icon('lock')}${t('Protected')}</span>` : escapeHtml(firstLine(h.snippet.expansion).slice(0, 60))}`,
            `${h.wasDeleted ? (h.existsNow ? t('Deleted, then created again') : t('Deleted')) : t('Earlier version')} · ${t('replaced {time}', { time: timeAgo(h.replacedAt) })}`,
            `<button class="btn btn--small" data-restore="${escapeHtml(h.key)}">${icon('history')}${t('Restore')}</button>`)).join('');
    }

    return `<div class="group"><div class="group-title">${t('Server history')}</div><div class="group-lead">${t('Earlier versions of changed or deleted snippets from the last 30 days. Decrypted here; the server cannot read them.')}</div>${body}</div>`;
}

function renderAccount() {
    const app = state.app;
    const sync = settings().sync;
    const page = $('[data-view="account"]');
    if (!app.sync.signedIn && state.browserSignIn) {
        page.innerHTML = `<div class="page-narrow">
            <h1 class="page-title">${t('Sync &amp; account')}</h1>
            <p class="page-lead">${t('Finish signing in on the website that just opened in your browser.')}</p>
            <div class="card signin">
                <p class="inline-note">${t('Check that the website shows this code before you enter your password:')}</p>
                <p class="fingerprint">${escapeHtml(state.browserSignIn.fingerprint)}</p>
                <p class="inline-note">${t('Your browser hands the encryption key to this app only; the password stays in the browser. The link works for 10 minutes.')}</p>
                <div class="actions">
                    <button class="btn" type="button" data-action="browser-signin">${icon('external')}${t('Open the page again')}</button>
                    <button class="btn btn--ghost" type="button" data-action="cancel-browser-signin">${t('Cancel')}</button>
                </div>
            </div>
        </div>`;
        return;
    }

    if (!app.sync.signedIn) {
        const customServer = sync.serverUrl !== sync.defaultServer;
        page.innerHTML = `<div class="page-narrow">
            <h1 class="page-title">${t('Sync &amp; account')}</h1>
            <p class="page-lead">${t('Sign in to keep your snippets on every computer. They are encrypted on this PC before upload; we cannot read them.')}</p>
            <form class="card signin" id="signin-form">
                <label class="field"><span>${t('Email')}</span><input class="input" name="email" type="email" value="${escapeHtml(sync.email)}" required autocomplete="username" spellcheck="false"></label>
                <label class="field"><span>${t('Password')}</span><input class="input" name="password" type="password" required autocomplete="current-password"></label>
                <label class="field"><span>${t('Name of this computer')}</span><input class="input" name="device" value="${escapeHtml(sync.deviceName)}" required></label>
                <label class="field" id="server-field" ${customServer ? '' : 'hidden'}><span>${t('Server')}</span><input class="input input--mono" name="server" value="${escapeHtml(sync.serverUrl)}" spellcheck="false"></label>
                <div class="error" hidden></div>
                <div class="actions">
                    <button class="btn btn--primary" type="submit">${t('Sign in')}</button>
                    <button class="btn" type="button" data-action="browser-signin">${icon('external')}${t('Sign in with browser')}</button>
                    ${customServer ? '' : `<button class="btn btn--ghost" type="button" data-action="custom-server">${t('Use another server')}</button>`}
                </div>
                <p class="inline-note u-mt-4">${t('No account yet?')} <button class="link" type="button" data-action="open-register">${t('Create one on freevy.app')}</button></p>
            </form>
        </div>`;
        return;
    }

    page.innerHTML = `<div class="page-narrow">
        <h1 class="page-title">${t('Sync &amp; account')}</h1>
        <p class="page-lead">${t('Your snippets are encrypted on this PC before they are uploaded.')}</p>
        <div class="card account">
            <div class="account-head"><span class="avatar">${escapeHtml((sync.email[0] ?? '?').toUpperCase())}</span><div><strong>${escapeHtml(sync.email)}</strong><span>${escapeHtml(t('This computer: {name}', { name: sync.deviceName }))}</span></div></div>
            <div class="stats">
                <div class="stat"><strong>${number.format(state.snippets.length)}</strong><span>${t('snippets')}</span></div>
                <div class="stat"><strong>${app.sync.running ? '…' : app.sync.lastSync ? timeAgo(app.sync.lastSync) : t('not yet')}</strong><span>${t('last sync')}</span></div>
                <div class="stat stat--accent"><strong>${t('{count} min', { count: sync.intervalMinutes })}</strong><span>${t('sync interval')}</span></div>
            </div>
            ${app.sync.error ? `<p class="inline-note inline-note--error u-mt-4">${escapeHtml(app.sync.error)}</p>` : ''}
            <div class="actions">
                <button class="btn btn--primary ${app.sync.running ? 'is-busy' : ''}" data-action="sync-now" ${app.sync.running ? 'disabled' : ''}>${icon('refresh')}${t('Sync now')}</button>
                <button class="btn" data-action="open-account">${icon('external')}${t('Devices on the website')}</button>
                <button class="btn btn--ghost" data-action="sign-out">${icon('logout')}${t('Sign out')}</button>
            </div>
        </div>
        <div class="group">
            ${row(t('Sync every'), t('Freevy also syncs a few seconds after you change something.'), `${numberInput('syncIntervalMinutes', sync.intervalMinutes, 1, 1440)}<span class="inline-note">${t('minutes')}</span>`)}
        </div>
    </div>`;
}

function renderSecurity() {
    const app = state.app;
    const protectedCount = state.snippets.filter((s) => s.protected).length;
    const s = settings();
    const main = !app.vault.configured
        ? `<form class="card signin" id="master-form">
                <h3 class="u-m0">${t('Set a master password')}</h3>
                <p class="inline-note u-mt-1">${t('Protected snippets are encrypted with it (AES-256). There is no recovery: if you forget it, protected snippets cannot be opened, also not from backups.')}</p>
                <label class="field u-mt-4"><span>${t('Master password (at least 8 characters)')}</span><input class="input" name="password" type="password" required minlength="8" autocomplete="new-password"></label>
                <label class="field"><span>${t('Repeat')}</span><input class="input" name="repeat" type="password" required autocomplete="new-password"></label>
                <label class="check u-mt-3"><input type="checkbox" name="protectFolders" checked><span>${t('Protect snippets in folders whose name contains “password”')}</span></label>
                <div class="error" hidden></div>
                <div class="actions"><button class="btn btn--primary" type="submit">${icon('shield')}${t('Set master password')}</button></div>
            </form>`
        : `<div class="cards">
                <div class="card ${app.vault.unlocked ? 'card--active' : ''}"><div class="card-icon">${icon(app.vault.unlocked ? 'unlock' : 'lock')}</div><h3>${app.vault.unlocked ? t('Unlocked until {time}', { time: app.vault.locksAt }) : t('Locked')}</h3><div class="meta">${plural(protectedCount, 'protected snippet')}</div></div>
            </div>
            <div class="actions">
                ${app.vault.unlocked
                    ? `<button class="btn btn--primary" data-action="lock">${icon('lock')}${t('Lock now')}</button>`
                    : `<button class="btn btn--primary" data-action="unlock">${icon('unlock')}${t('Unlock')}</button>`}
                <button class="btn" data-action="change-master">${icon('key')}${t('Change master password')}</button>
                <button class="btn btn--ghost btn--danger" data-action="remove-master">${t('Remove master password')}</button>
            </div>`;

    $('[data-view="security"]').innerHTML = `<div class="page-narrow">
        <h1 class="page-title">${t('Master password')}</h1>
        <p class="page-lead">${t('Protected snippets (passwords, card numbers, anything secret) are encrypted on this PC and only unlock for a few minutes at a time.')}</p>
        ${main}
        <div class="group">
            <div class="group-title">${t('Unlocking')}</div>
            ${row(t('Stay unlocked for'), t('After entering the master password.'), `${numberInput('unlockMinutes', s.unlockMinutes, 1, 1440)}<span class="inline-note">${t('minutes')}</span>`)}
            ${row(t('Lock when Windows locks'), t('Forget the key as soon as you lock your PC.'), toggle('lockWhenWindowsLocks', s.lockWhenWindowsLocks, t('Lock when Windows locks')))}
        </div>
    </div>`;
}

// Current version, what the last check found, and the one action that fits.
function updateRow() {
    const u = state.app.update ?? {};
    const version = escapeHtml(state.app.version ?? '');
    if (!u.installed) {
        return row(t('Version {version}', { version }), t('Running from a build folder; updates only work in the installed app.'), '');
    }

    const checked = u.lastChecked ? t('Last checked {time}.', { time: timeAgo(u.lastChecked) }) : t('Not checked yet.');
    if (u.ready) {
        return row(t('Freevy {version} is ready', { version: escapeHtml(u.ready) }), t('You have {version}. It installs the next time Freevy starts, or now with a quick restart.', { version }),
            `<button class="btn btn--small btn--primary" data-action="restart-to-update">${icon('download')}${t('Restart and update')}</button>`);
    }
    if (u.available) {
        return row(t('Freevy {version} is available', { version: escapeHtml(u.available) }), `${t('You have {version}.', { version })} ${checked}`,
            `<button class="btn btn--small btn--primary" data-action="install-update" ${u.checking ? 'disabled' : ''}>${icon('download')}${t('Download and install')}</button>`);
    }

    return row(t('Version {version}', { version }), u.checking ? t('Checking for updates…') : u.error ? escapeHtml(t(u.error)) : `${checked}${u.lastChecked ? ` ${t('You have the latest version.')}` : ''}`,
        `<button class="btn btn--small" data-action="check-updates" ${u.checking ? 'disabled' : ''}>${t('Check now')}</button>`);
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
        <h1 class="page-title">${t('Settings')}</h1>
        <p class="page-lead">${t('How Freevy types, starts and looks.')}</p>

        <div class="group">
            <div class="group-title">${t('General')}</div>
            ${row(t('Language'), t('Follows Windows unless you choose one.'), `<select class="input input--medium" data-setting-select="language" aria-label="${t('Language')}">${[['', t('Like Windows')], ['en', 'English'], ['tr', 'Türkçe'], ['de', 'Deutsch']].map(([v, label]) => `<option value="${v}" ${v === (s.language ?? '') ? 'selected' : ''}>${label}</option>`).join('')}</select>`)}
            ${row(t('Expand abbreviations'), t('Pause Freevy without closing it. Also on the chip in the title bar.'), toggle('expansionEnabled', s.expansionEnabled, t('Expand abbreviations')))}
            ${row(t('Start with Windows'), t('Freevy starts in the tray when you sign in to Windows.'), toggle('autostart', s.autostart, t('Start with Windows')))}
            ${row(t('Updates'), t('A few times a day Freevy asks freevy.app for a new version. The request carries your Freevy version and nothing else about you.'), segmented(t('Updates'), 'updateMode', s.updateMode, [['install', t('Install automatically')], ['notify', t('Only tell me')], ['off', t('Off')]]))}
            ${s.updateMode === 'off' ? '' : row(t('Check every'), '', segmented(t('Check every'), 'updateCheckHours', String(s.updateCheckHours), [['1', t('Hour')], ['6', t('6 hours')], ['24', t('Day')], ['168', t('Week')]]))}
            ${updateRow()}
        </div>

        <div class="group">
            <div class="group-title">${t('Typing')}</div>
            ${row(t('Delimiters'), t('Characters that end a word. \\t is Tab, \\n is Enter.'), `<input class="input input--medium input--mono" data-setting="delimiters" value="${escapeHtml(s.delimiters)}" spellcheck="false"><button class="btn btn--small btn--ghost" data-action="reset-delimiters">${t('Reset')}</button>`)}
            ${row(t('Line breaks'), t('Typing Enter sends the message in Slack, Discord or Teams. Pasting puts multi-line snippets in as one piece.'), segmented(t('Line breaks'), 'lineBreaks', s.lineBreaks, [['paste', t('Paste the text')], ['shiftEnter', t('Type Shift+Enter')], ['enter', t('Type Enter')]]))}
            ${row(t('Paste long expansions'), t('Expansions longer than this are pasted instead of typed.'), `${numberInput('clipboardThreshold', s.clipboardThreshold, 1, 1000000)}<span class="inline-note">${t('characters')}</span>`)}
            ${row(t('New snippets expand'), '', segmented(t('Default expand'), 'defaultTrigger', s.defaultTrigger, [['after', t('After space')], ['instant', t('Instantly')]]))}
            ${row(t('New snippets only at word start'), '', toggle('defaultWordStart', s.defaultWordStart, t('Only at word start')))}
            ${row(t('New snippets case'), '', segmented(t('Default case'), 'defaultCase', s.defaultCase, [['exact', t('Exact')], ['ignore', t('Ignore')], ['adapt', t('Adapt')]]))}
        </div>

        <div class="group">
            <div class="group-title">${t('Privacy')}</div>
            ${row(t('Hide expansions in folders containing'), t('Only hides text in the list. Use protected snippets to encrypt secrets.'), `<input class="input input--medium" data-setting="maskFoldersContaining" value="${escapeHtml(s.maskFoldersContaining)}" placeholder="${t('e.g. private')}">`)}
        </div>

        <h2 class="section-title">${icon('palette')}${t('Theme')}</h2>
        <div class="theme-group-label">${t('Dark')}</div>
        <div class="theme-grid" role="radiogroup" aria-label="${t('Dark themes')}">${THEMES.filter((t) => t[2] === 'dark').map(themeCard).join('')}</div>
        <div class="theme-group-label">${t('Light')}</div>
        <div class="theme-grid" role="radiogroup" aria-label="${t('Light themes')}">${THEMES.filter((t) => t[2] === 'light').map(themeCard).join('')}</div>

        <div class="group">
            <div class="group-title">${t('Your data')}</div>
            ${row(t('Import from Breevy'), state.app.breevyAvailable ? 'Replaces your snippets with your Breevy library. The current ones are backed up first.' : 'No Breevy library found on this PC.', `<button class="btn btn--small" data-action="import-breevy" ${state.app.breevyAvailable ? '' : 'disabled'}>${icon('download')}${t('Import')}</button>`)}
            ${row(t('Import from another app'), t('Espanso, Beeftext, TextExpander (CSV export) or any CSV file. Adds to your snippets; nothing is replaced.'), `<button class="btn btn--small" data-action="import-other" aria-haspopup="menu">${icon('download')}${t('Import…')}</button>`)}
            ${row(t('Export snippets'), t('A JSON file with all snippets; protected ones stay encrypted.'), `<button class="btn btn--small" data-action="export">${icon('upload')}${t('Export…')}</button>`)}
            ${row(t('Data folder'), escapeHtml(state.app.dataDirectory), `<button class="btn btn--small" data-action="open-data">${icon('folder')}${t('Open')}</button>`)}
        </div>
        <div class="group">
            <div class="group-title">${t('About')}</div>
            ${row(`Freevy ${escapeHtml(state.app.version ?? '')}`, `${state.app.commit ? `${t('Built from commit {commit}.', { commit: escapeHtml(state.app.commit) })} ` : ''}${isDemo ? `${t('Demo data.')} ` : ''}${t('Free and open source under the GNU GPL v3.')}`,
                `<button class="btn btn--small" data-link="releaseNotes">${icon('external')}${t('What\'s new')}</button><button class="btn btn--small btn--ghost" data-link="license">${t('License')}</button>`)}
            ${row(t('Freevy on the web'), t('No telemetry and no ads. Ideas and bug reports are welcome on GitHub.'),
                `<button class="btn btn--small" data-link="website">freevy.app</button><button class="btn btn--small" data-action="open-github">${icon('github')}GitHub</button><button class="btn btn--small" data-link="issues">${t('Report a problem')}</button><button class="btn btn--small btn--ghost" data-link="privacy">${t('Privacy')}</button>`)}
        </div>
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
    if (name === 'snippets') {
        renderList();
        // The setting may have changed meanwhile; relabel without redrawing the editor and its unsaved edits.
        const option = $('#editor [data-field="lineBreaks"] option[value="default"]');
        if (option) option.textContent = defaultLineBreakLabel();
    }
}

// ---- Settings changes -------------------------------------------------------------

/** "backup.sftp.host" + value becomes { backup: { sftp: { host: value } } } for saveSettings. */
function nested(path, value) {
    return path.split('.').reduceRight((inner, key) => ({ [key]: inner }), value);
}

async function saveSetting(path, value) {
    if (path === 'autostart') {
        await run(() => call('setAutostart', { enabled: value }), value ? t('Freevy starts with Windows') : t('Freevy no longer starts with Windows'));
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
    if (target.dataset.link) return call('openLink', { link: target.dataset.link });
    if (target.dataset.window) return call('window', { action: target.dataset.window });
    if (target.matches('.folder-item')) {
        state.folder = target.dataset.folder === ALL ? null : target.dataset.folder;
        showView('snippets');
        renderFolders();
        $('#list').scrollTop = 0;
        return renderList();
    }
    if (target.dataset.folderMenu !== undefined) return folderMenu(target, target.dataset.folderMenu);
    if (target.matches('.snippet-row')) {
        const id = target.dataset.id;
        // In edit mode the whole row selects; the eye opens the snippet. Ctrl+click and Shift+click work in both modes.
        if (state.editMode && event.target.closest('.row-open')) return select(id);
        if (event.shiftKey && (state.editMode || state.selectedId)) return selectRange(id);
        if (state.editMode || event.ctrlKey || event.metaKey) return toggleChecked(id, event.ctrlKey || event.metaKey);
        return select(id);
    }
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
        const path = await call('chooseFolder', { title: t('Choose backup folder'), initial: '' });
        if (path) await saveSetting(target.dataset.choose, path);
        return renderPage();
    }

    if (target.dataset.chooseFile) {
        const path = await call('chooseFile', { title: t('Choose SSH private key'), filter: `${t('All files')} (*.*)|*.*`, initial: '' });
        if (path) await saveSetting(target.dataset.chooseFile, path);
        return renderPage();
    }

    if (target.dataset.restore) {
        const version = state.history.find((h) => h.key === target.dataset.restore);
        const abbr = version.snippet.abbreviations[0];
        const body = version.existsNow ? t('The current “{name}” is replaced by this version. The current one stays in the history.', { name: escapeHtml(abbr) }) : t('“{name}” comes back as it was.', { name: escapeHtml(abbr) });
        if (await confirmDialog({ title: t('Restore this version?'), body, confirm: t('Restore') })) {
            const restored = await run(() => call('restoreVersion', { key: version.key }), t('Restored “{name}”', { name: abbr }));
            if (restored) {
                state.history = null;
                renderBackups();
            }
        }
        return;
    }

    switch (action) {
        case 'save': return save();
        case 'discard': return discardChanges();
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
            const path = await call('chooseFile', { title: t('Restore from backup'), filter: `${t('Freevy backups')} (*.zip)|*.zip`, initial: settings().backup.localDirectory });
            if (path && await confirmDialog({ title: t('Restore this backup?'), body: t('All current snippets are replaced. If it contains protected snippets, its master password applies again. The current state is saved first as a backup.'), confirm: t('Restore'), danger: true })) {
                await run(() => call('restoreBackup', { path }), (n) => t('Restored {count}', { count: plural(n, 'snippet') }));
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
            await run(() => call('trustHostKey', { fingerprint: state.sftpProbe.fingerprint }), t('Host key trusted'));
            state.sftpProbe = null;
            await refreshState();
            return renderBackups();
        case 'custom-server':
            $('#server-field').hidden = false;
            target.remove();
            return;
        case 'browser-signin': {
            // Uses the computer name and server from the form when it is on screen.
            const form = $('#signin-form');
            const device = String(form?.elements.device.value || settings().sync.deviceName).trim();
            const server = String(form?.elements.server.value || settings().sync.serverUrl).trim();
            const started = await run(() => call('signInWithBrowser', { server, device }));
            if (started) {
                state.browserSignIn = started;
                renderPage();
            }
            return;
        }
        case 'cancel-browser-signin':
            await call('cancelBrowserSignIn');
            state.browserSignIn = null;
            renderPage();
            return;
        case 'restart-to-update': return call('restartToUpdate');
        case 'check-updates': {
            target.disabled = true;
            state.app = (await run(() => call('checkForUpdates'))) ?? state.app;
            const u = state.app.update ?? {};
            if (!u.ready && !u.available && !u.error) toast(t('Freevy is up to date'));
            renderPage();
            return;
        }
        case 'install-update': {
            target.disabled = true;
            target.textContent = t('Downloading…');
            state.app = (await run(() => call('installUpdate'))) ?? state.app;
            renderPage();
            return;
        }
        case 'edit-mode': return setEditMode(true);
        case 'edit-done': return setEditMode(false);
        case 'select-all': {
            const allVisible = state.visible.every((s) => state.checked.has(s.id));
            state.visible.forEach((s) => (allVisible ? state.checked.delete(s.id) : state.checked.add(s.id)));
            return renderList();
        }
        case 'select-extra': {
            const extra = extraCopies();
            if (!extra.length) return toast(t('No exact copies here: these snippets differ, so choose which to keep yourself'));
            extra.forEach((id) => state.checked.add(id));
            renderList();
            return toast(t('{count} selected; one of each is kept', { count: plural(extra.length, 'exact copy', 'exact copies') }));
        }
        case 'delete-checked': return deleteChecked();
        case 'move-checked': return moveChecked();
        case 'insert-macro': return insertMacro(target);
        case 'open-register': return call('openAccountPage', { path: 'register' });
        case 'open-account': return call('openAccountPage', { path: 'account' });
        case 'sync-now': return call('syncNow');
        case 'sign-out':
            if (await confirmDialog({ title: t('Sign this computer out?'), body: t('Your snippets stay on this PC; they just stop syncing.'), confirm: t('Sign out') })) {
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
            if (await confirmDialog({ title: t('Import from Breevy?'), body: t('Your current snippets are replaced by your Breevy library. They are backed up first.'), confirm: t('Import') })) {
                await run(() => call('importBreevy'), (n) => t('Imported {count}', { count: plural(n, 'snippet') }));
            }
            return;
        case 'export': {
            const path = await run(() => call('exportSnippets'));
            if (path) toast(t('Snippets exported'));
            return;
        }
        case 'open-data': return call('openDataFolder');
        case 'open-github': return call('openRepository');
        case 'import-other': return menu(target, [
            [t('Espanso (match folder)'), 'Espanso', 'espanso'],
            [t('Beeftext (comboList.json)'), 'Beeftext', 'beeftext'],
            [t('TextExpander (group exported as CSV)'), 'TextExpander', 'textexpander'],
            [t('Other CSV file'), t('the CSV file'), 'csv'],
        ].map(([label, source, kind]) => ({ label, icon: 'download', action: () => importFrom(kind, source) })));
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
        title: t('Change master password'),
        body: t('Protected snippets are re-encrypted with the new password.'),
        fields: [
            { label: t('Current master password'), type: 'password', autocomplete: 'current-password' },
            { label: t('New master password (at least 8 characters)'), type: 'password', autocomplete: 'new-password' },
            { label: t('Repeat new master password'), type: 'password', autocomplete: 'new-password' },
        ],
        confirm: t('Change'),
        onSubmit: async ({ fields }) => {
            if (fields[1] !== fields[2]) throw new Error(t('The new passwords do not match.'));
            await call('setMasterPassword', { current: fields[0], password: fields[1] });
        },
    });
    if (result) toast(t('Master password changed'));
}

async function removeMaster() {
    const count = state.snippets.filter((s) => s.protected).length;
    const result = await dialog({
        title: t('Remove master password?'),
        body: t('{count} will be stored as plain text again.', { count: plural(count, 'protected snippet') }),
        fields: [{ label: t('Master password'), type: 'password', autocomplete: 'current-password' }],
        confirm: t('Remove'),
        danger: true,
        onSubmit: ({ fields }) => call('removeMasterPassword', { password: fields[0] }),
    });
    if (result) toast(t('Master password removed'));
}

document.addEventListener('change', async (event) => {
    const input = event.target;
    if (input.dataset.settingSelect === 'language') {
        // Everything on screen was rendered in the old language: start over in the new one.
        await saveSetting('language', input.value);
        location.reload();
        return;
    }

    if (input.dataset.setting && input.type !== 'checkbox') {
        const value = input.type === 'number' ? Number(input.value) : input.value;
        return saveSetting(input.dataset.setting, value);
    }

    if (input.dataset.secret) {
        await run(() => call('setSftpSecret', { [input.dataset.secret]: input.value }), input.value ? t('Saved securely') : t('Removed'));
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
    if (field && state.draft && ['description', 'expansion', 'folder', 'lineBreaks'].includes(field)) {
        state.draft[field] = input.value;
        updateEditorChrome();
        if (field === 'folder') $('#editor .eyebrow').textContent = input.value || t('No folder');
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
            toast(t('Signed in. Syncing your snippets…'));
        } else if (form.id === 'master-form') {
            if (data.get('password') !== data.get('repeat')) throw new Error(t('The two passwords do not match.'));
            const protectedCount = await call('setMasterPassword', { password: data.get('password'), protectPasswordFolders: data.get('protectFolders') === 'on' });
            toast(protectedCount ? t('Master password set. {count} protected.', { count: plural(protectedCount, 'snippet') }) : t('Master password set'));
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

    // Edit mode: Ctrl+A ticks everything shown, Delete removes the ticked snippets, Escape leaves.
    const typing = event.target.matches?.('input, textarea, select, [contenteditable="true"]');
    if (state.editMode && state.view === 'snippets' && !typing && !document.querySelector('dialog[open]')) {
        if (ctrl && key === 'a') {
            event.preventDefault();
            state.visible.forEach((s) => state.checked.add(s.id));
            return renderList();
        }
        if (event.key === 'Delete') { event.preventDefault(); return deleteChecked(); }
        if (event.key === 'Escape') { event.preventDefault(); return setEditMode(false); }
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
$('#edit-toggle').addEventListener('click', () => setEditMode(!state.editMode));
$('#app-version').addEventListener('click', () => showView('settings'));
$('#chip-github').addEventListener('click', () => call('openRepository'));
$('#chip-sync').addEventListener('click', () => (state.app.sync.signedIn ? call('syncNow') : showView('account')));
$('#chip-vault').addEventListener('click', async () => {
    if (state.app.vault.unlocked) await call('lock');
    else await unlockDialog();
});

// ---- Live updates from the host ------------------------------------------------------

on('change', async (kind) => {
    await refreshState();
    if (state.app.sync.signedIn) state.browserSignIn = null;
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

on('notice', ({ message, error }) => toast(t(message), error));

async function renderGitHubStars() {
    const stars = await call('getGitHubStars').catch(() => null);
    const badge = $('#github-stars');
    badge.hidden = stars === null || stars === undefined;
    if (!badge.hidden) {
        $('span', badge).textContent = stars >= 1000 ? `${(stars / 1000).toFixed(1).replace(/\.0$/, '')}k` : number.format(stars);
        $('#chip-github').title = t('Star Freevy on GitHub ({count})', { count: plural(stars, 'star') });
    }
}

// Asked once, the first time the window opens after Freevy saved 100 keystrokes.
let starAsked = false;
async function maybeAskForStar() {
    if (starAsked || !state.app.starPrompt) return;
    starAsked = true;
    const star = await confirmDialog({
        title: t('Enjoying Freevy?'),
        body: t('Freevy already saved you {count}. It is free and open source, built by a small community. A star on GitHub helps other people find it.', { count: plural(state.app.stats.totalKeystrokesSaved, 'keystroke') }),
        confirm: t('Star on GitHub'),
        cancel: t('No thanks'),
    });
    await call('starPromptDone', { open: star });
    state.app.starPrompt = false;
}

on('opened', async () => {
    await refreshState();
    renderChrome();
    renderGitHubStars();
    await maybeAskForStar();
});

// ---- Start ------------------------------------------------------------------------------

await Promise.all([refreshState(), refreshSnippets()]);
await setLanguage(settings().language);
translateStatic(document.body);
applyTheme(settings().theme, false);
renderAllSnippetParts();
filterSnippets();
if (state.visible.length > 0) {
    await select(state.visible[0].id);
}

setInterval(renderChrome, 30000);
renderGitHubStars();
await maybeAskForStar();
