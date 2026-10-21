// Lists the English texts the app translates and compares them with src/Freevy.App/ui/i18n/<lang>.json.
//
//   node build/i18n-check.mjs            report missing and unused keys (exit code 1 when any are missing)
//   node build/i18n-check.mjs --keys     print every key, one JSON string per line
//
// Keys come from t('...') in the pages, L.T("...") in C#, plural() words, data-i18n markup, and the
// fixed exception messages that reach the pages and are translated there.
import { readFileSync, readdirSync, statSync } from 'node:fs';
import { join, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = join(dirname(fileURLToPath(import.meta.url)), '..', 'src');
const ui = join(root, 'Freevy.App', 'ui');
const languages = ['tr', 'de'];

const files = (dir, ext) => readdirSync(dir).flatMap((name) => {
    const path = join(dir, name);
    if (statSync(path).isDirectory()) return ['bin', 'obj'].includes(name) ? [] : files(path, ext);
    return path.endsWith(ext) ? [path] : [];
});

const unescapeJs = (s) => s.replace(/\\(['"`\\])/g, '$1');
const keys = new Set();
const add = (key) => key && /[A-Za-z]/.test(key) && keys.add(key);

for (const file of files(join(ui, 'js'), '.js').filter((f) => !f.endsWith('bridge.js') && !f.endsWith('i18n.js'))) {
    const src = readFileSync(file, 'utf8');
    for (const m of src.matchAll(/\bt\((['`])((?:\\.|(?!\1)[^\\])*)\1/g)) if (!m[2].includes('${')) add(unescapeJs(m[2]));
    for (const m of src.matchAll(/\bt\([\w.]+ \? '((?:\\.|[^'\\])*)' : '((?:\\.|[^'\\])*)'/g)) [m[1], m[2]].forEach((k) => add(unescapeJs(k)));
    for (const m of src.matchAll(/\bplural\([^,]+,\s*'([^']+)'(?:,\s*'([^']+)')?\)/g)) {
        add(m[1]);
        add(m[2] ?? `${m[1]}s`);
    }
    // Label tables translated where they are used: LINE_BREAK_OPTIONS, DATE_PRESETS.
    for (const table of src.matchAll(/const (?:LINE_BREAK_OPTIONS|DATE_PRESETS) = \[([\s\S]*?)\];/g)) {
        for (const m of table[1].matchAll(/\[\s*'[^']*',\s*'((?:\\.|[^'\\])*)'\s*\]/g)) add(unescapeJs(m[1]));
    }
    if (src.includes("'Days', 'Months', 'Years', 'Hours', 'Minutes', 'Seconds'")) ['days', 'months', 'years', 'hours', 'minutes', 'seconds'].forEach(add);
}

for (const file of files(ui, '.html')) {
    const src = readFileSync(file, 'utf8');
    for (const m of src.matchAll(/<[^>]*\bdata-i18n\b[^>]*>([^<]*)/g)) add(m[1].trim());
    for (const m of src.matchAll(/<[^>]*\bdata-i18n-attr\b[^>]*>/g)) {
        for (const a of m[0].matchAll(/\b(?:title|aria-label|placeholder)="([^"]*)"/g)) add(a[1]);
    }
}

for (const file of files(root, '.cs')) {
    const src = readFileSync(file, 'utf8');
    for (const m of src.matchAll(/L\.T\("((?:\\.|[^"\\])*)"/g)) add(m[1].replace(/\\"/g, '"'));
    // Fixed messages that the pages show through t(failure.message).
    for (const m of src.matchAll(/new (?:InvalidOperationException|InvalidDataException|FreevyApiException|SignedOutException|UserError)\("((?:\\.|[^"\\])*)"/g)) add(m[1].replace(/\\"/g, '"'));
    for (const m of src.matchAll(/new ToolStripMenuItem\("((?:\\.|[^"\\])*)"/g)) add(m[1]);
    for (const m of src.matchAll(/new BackupResult\(false, "((?:\\.|[^"\\])*)"\)/g)) add(m[1]);
    for (const m of src.matchAll(/new FolderBackupTarget\("([^"]+)"/g)) add(m[1]);
    for (const m of src.matchAll(/HttpStatusCode\.\w+ => "((?:\\.|[^"\\])*)"/g)) add(m[1]);
}

if (process.argv.includes('--keys')) {
    [...keys].sort().forEach((k) => console.log(JSON.stringify(k)));
    process.exit(0);
}

let missingTotal = 0;
for (const lang of languages) {
    const dictionary = JSON.parse(readFileSync(join(ui, 'i18n', `${lang}.json`), 'utf8'));
    const missing = [...keys].filter((k) => !(k in dictionary) || !dictionary[k]);
    const unused = Object.keys(dictionary).filter((k) => !keys.has(k));
    // A translation must keep every {placeholder} of its key.
    const broken = Object.entries(dictionary).filter(([k, v]) => keys.has(k) && [...k.matchAll(/\{\w+\}/g)].some((p) => !v.includes(p[0])));
    missingTotal += missing.length + broken.length;
    console.log(`${lang}: ${keys.size} keys, ${missing.length} missing, ${unused.length} unused, ${broken.length} with lost placeholders`);
    missing.forEach((k) => console.log(`  missing: ${JSON.stringify(k)}`));
    unused.forEach((k) => console.log(`  unused:  ${JSON.stringify(k)}`));
    broken.forEach(([k]) => console.log(`  placeholders: ${JSON.stringify(k)}`));
}
process.exit(missingTotal ? 1 : 0);
