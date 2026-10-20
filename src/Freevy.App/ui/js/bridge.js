// Talks to the C# host through WebView2. In a normal browser (design review, UI tests)
// a small in-memory demo backend answers instead, so every screen still works.

const host = window.chrome?.webview;
const listeners = new Map();
const pending = new Map();
let nextId = 1;

function emit(name, data) {
    (listeners.get(name) ?? []).forEach((handler) => handler(data));
}

if (host) {
    host.addEventListener('message', (event) => {
        const message = typeof event.data === 'string' ? JSON.parse(event.data) : event.data;
        if (message.event) {
            emit(message.event, message.data);
            return;
        }

        const request = pending.get(message.id);
        if (request) {
            pending.delete(message.id);
            message.ok ? request.resolve(message.result) : request.reject(new Error(message.error));
        }
    });
}

export const isDemo = !host;

/** Calls a C# method; rejects with the user-facing error message. */
export function call(method, params = {}) {
    if (!host) {
        return demo(method, params);
    }

    return new Promise((resolve, reject) => {
        const id = nextId++;
        pending.set(id, { resolve, reject });
        host.postMessage(JSON.stringify({ id, method, params }));
    });
}

export function on(name, handler) {
    listeners.set(name, [...(listeners.get(name) ?? []), handler]);
}

// ---- Demo backend ---------------------------------------------------------------

const wait = (ms) => new Promise((resolve) => setTimeout(resolve, ms));
const uuid = () => crypto.randomUUID();
const store = {
    snippets: [
        ['Emails', [';sig', ';regards'], 'Kind regards,\nJane Doe\nSupport Lead · Example Ltd.\n+1 555 0100', 'Email signature'],
        ['Typo AutoCorrections', ['teh', 'hte'], 'the', ''],
        ['Typo AutoCorrections', ['adn'], 'and', ''],
        ['Typo AutoCorrections', ['recieve'], 'receive', ''],
        ['Emails', ['@@'], 'jane.doe@example.com', 'Email address'],
        ['Textmojis', ['shrug'], '¯\\_(ツ)_/¯', ''],
        ['Code', ['dd'], 'console.log()', ''],
        ['Games/WoW', ['wowgg'], 'Good game, well played!', ''],
        ['Others', ['addr'], '221B Example Street, London', 'Home address'],
    ].map(([folder, abbreviations, expansion, description]) => ({
        id: uuid(), abbreviations, expansion, description, folder, trigger: 'after', wordStart: true, case: 'adapt', protected: false, modified: new Date().toISOString(),
    })).concat([{ id: uuid(), abbreviations: ['gmailpw'], expansion: null, description: 'Gmail', folder: 'Passwords', trigger: 'after', wordStart: true, case: 'exact', protected: true, modified: new Date().toISOString() }]),
    secrets: new Map(),
    vault: { configured: true, unlocked: false, locksAt: null },
    signedIn: false,
    settings: {
        expansionEnabled: true, delimiters: ',.?!()<>[]{}:;"/\\\\-\' \\t\\n', clipboardThreshold: 3500, defaultTrigger: 'after', defaultWordStart: true, defaultCase: 'adapt',
        maskFoldersContaining: '', unlockMinutes: 5, lockWhenWindowsLocks: true, theme: 'freevy', autostart: false,
        backup: {
            enabled: true, intervalHours: 24, onChange: true, keepCount: 10, localEnabled: true, localDirectory: 'C:\\Users\\you\\AppData\\Roaming\\Freevy\\backups',
            folderEnabled: false, folderDirectory: '', sftp: { enabled: false, host: '', port: 22, username: '', hasPassword: false, privateKeyPath: '', hasPassphrase: false, remoteDirectory: 'Freevy-backups', hostKeyFingerprint: '' },
        },
        sync: { serverUrl: 'https://freevy.app', email: '', deviceName: 'DEMO-PC', intervalMinutes: 5, defaultServer: 'https://freevy.app' },
    },
    backup: { running: false, lastRun: null, lastSuccess: null, result: '' },
    stats: { today: { expansions: 37, keystrokesSaved: 1284 }, totalExpansions: 5120, totalKeystrokesSaved: 182304 },
};
store.secrets.set(store.snippets.at(-1).id, 'correct-horse-battery');

function demoState() {
    return {
        version: 'demo', dataDirectory: 'C:\\Users\\you\\AppData\\Roaming\\Freevy', breevyAvailable: true,
        settings: structuredClone(store.settings), vault: { ...store.vault },
        sync: { signedIn: store.signedIn, running: false, lastSync: store.signedIn ? new Date().toISOString() : null, error: null },
        backup: { ...store.backup }, stats: structuredClone(store.stats), abbreviationCount: store.snippets.reduce((n, s) => n + s.abbreviations.length, 0),
    };
}

function requireUnlocked() {
    if (!store.vault.unlocked) {
        throw new Error('Unlock your protected snippets first.');
    }
}

function changed(kind) {
    setTimeout(() => emit('change', kind), 0);
}

async function demo(method, p) {
    await wait(60);
    switch (method) {
        case 'getState': return demoState();
        case 'getSnippets': return structuredClone(store.snippets);
        case 'saveSnippet': {
            const abbreviations = p.abbreviations.map((a) => a.trim()).filter(Boolean);
            if (abbreviations.length === 0) throw new Error('Add at least one abbreviation.');
            if (p.protected && p.expansion !== null) requireUnlocked();
            const existing = store.snippets.find((s) => s.id === p.id);
            const snippet = { ...(existing ?? { id: uuid() }), ...p, abbreviations, id: existing?.id ?? uuid(), modified: new Date().toISOString() };
            if (p.protected) {
                if (p.expansion !== null) store.secrets.set(snippet.id, p.expansion);
                snippet.expansion = null;
            } else if (p.expansion === null) {
                requireUnlocked();
                snippet.expansion = store.secrets.get(snippet.id) ?? '';
            }
            if (existing) Object.assign(existing, snippet); else store.snippets.push(snippet);
            changed('snippets');
            return structuredClone(snippet);
        }
        case 'deleteSnippets': store.snippets = store.snippets.filter((s) => !p.ids.includes(s.id)); changed('snippets'); return null;
        case 'revealSnippet': requireUnlocked(); return store.secrets.get(p.id) ?? '';
        case 'renameFolder': {
            let n = 0;
            store.snippets.forEach((s) => { if (s.folder === p.folder || s.folder.startsWith(p.folder + '/')) { s.folder = p.name + s.folder.slice(p.folder.length); n++; } });
            changed('snippets');
            return n;
        }
        case 'deleteFolder': {
            const before = store.snippets.length;
            store.snippets = store.snippets.filter((s) => !(s.folder === p.folder || s.folder.startsWith(p.folder + '/')));
            changed('snippets');
            return before - store.snippets.length;
        }
        case 'protectFolder': requireUnlocked(); store.snippets.filter((s) => s.folder === p.folder).forEach((s) => { if (p.protect && !s.protected) { store.secrets.set(s.id, s.expansion); s.expansion = null; } else if (!p.protect && s.protected) { s.expansion = store.secrets.get(s.id) ?? ''; } s.protected = p.protect; }); changed('snippets'); return 1;
        case 'unlock': {
            if (p.password !== 'demo-master') return false;
            store.vault = { configured: true, unlocked: true, locksAt: new Date(Date.now() + (p.minutes ?? 5) * 60000).toTimeString().slice(0, 5) };
            changed('vault');
            return true;
        }
        case 'lock': store.vault = { ...store.vault, unlocked: false, locksAt: null }; changed('vault'); return null;
        case 'setMasterPassword':
            if (p.password.length < 8) throw new Error('The master password needs at least 8 characters.');
            store.vault = { configured: true, unlocked: true, locksAt: '23:59' }; changed('vault'); return 0;
        case 'removeMasterPassword': store.vault = { configured: false, unlocked: false, locksAt: null }; changed('vault'); return 0;
        case 'saveSettings': {
            const { backup, ...rest } = p;
            Object.assign(store.settings, rest);
            if (backup) {
                const { sftp, ...b } = backup;
                Object.assign(store.settings.backup, b);
                if (sftp) Object.assign(store.settings.backup.sftp, sftp);
            }
            changed('settings');
            return demoState();
        }
        case 'setTheme': store.settings.theme = p.theme; return null;
        case 'setAutostart': store.settings.autostart = p.enabled; return p.enabled;
        case 'backupNow':
            store.backup = { ...store.backup, running: true }; changed('backup');
            setTimeout(() => { store.backup = { running: false, lastRun: new Date().toISOString(), lastSuccess: new Date().toISOString(), result: 'Backed up to Local folder.' }; emit('change', 'backup'); }, 900);
            return null;
        case 'restoreBackup': return store.snippets.length;
        case 'setSftpSecret': if ('password' in p) store.settings.backup.sftp.hasPassword = p.password.length > 0; return demoState();
        case 'testSftp': await wait(600); if (!p.backup?.sftp?.host) throw new Error('Enter the host and user first.'); return { fingerprint: 'q2Vx8Xb1+demo+fingerprint+mN0aZ4TfY7w', trusted: false, changed: false };
        case 'trustHostKey': store.settings.backup.sftp.hostKeyFingerprint = p.fingerprint; return demoState();
        case 'chooseFolder': return 'C:\\Users\\you\\Google Drive\\My Drive\\Freevy backups';
        case 'chooseFile': return 'C:\\Users\\you\\Downloads\\example.zip';
        case 'exportSnippets': return 'C:\\Users\\you\\Documents\\Freevy-snippets.json';
        case 'importBreevy': return store.snippets.length;
        case 'openDataFolder': case 'openAccountPage': case 'window': case 'syncNow': return null;
        case 'getHistory':
            await wait(500);
            return [
                { key: 'h1', replacedAt: new Date(Date.now() - 3600e3).toISOString(), wasDeleted: true, existsNow: false, snippet: { id: uuid(), abbreviations: ['omw'], expansion: 'On my way!', description: '', folder: 'Chat', trigger: 'after', wordStart: true, case: 'adapt', protected: false } },
                { key: 'h2', replacedAt: new Date(Date.now() - 86400e3).toISOString(), wasDeleted: false, existsNow: true, snippet: { ...structuredClone(store.snippets[0]), expansion: 'Best, Jane' } },
            ];
        case 'restoreVersion': changed('snippets'); return {};
        case 'signIn':
            await wait(700);
            if (p.password !== 'demo-account') throw new Error('These credentials do not match our records.');
            store.signedIn = true; store.settings.sync.email = p.email; changed('sync'); return demoState();
        case 'signOut': store.signedIn = false; changed('sync'); return demoState();
        default: throw new Error(`Unknown request '${method}'.`);
    }
}
