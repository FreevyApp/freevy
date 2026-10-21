// Translations for the app's pages. The English text is the key: t('Snippet saved') looks it up in
// ui/i18n/<language>.json and falls back to the English text when a translation is missing.
// Placeholders are written {name} and filled from the params object.

export const LANGUAGES = ['en', 'tr', 'de'];

let strings = {};
let locale = 'en';

/** '' (or anything unknown) follows the system language, otherwise one of LANGUAGES. */
export function resolveLanguage(choice) {
    const pick = String(choice || navigator.language || 'en').slice(0, 2).toLowerCase();
    return LANGUAGES.includes(pick) ? pick : 'en';
}

export async function setLanguage(choice) {
    locale = resolveLanguage(choice);
    document.documentElement.lang = locale;
    strings = {};
    if (locale === 'en') return;
    try {
        strings = await (await fetch(`i18n/${locale}.json`)).json();
    } catch {
        // A missing or broken file leaves the app in English rather than unusable.
        strings = {};
    }
}

export const currentLocale = () => locale;

export function t(text, params) {
    const template = strings[text] ?? text;
    return params ? template.replace(/\{(\w+)\}/g, (match, key) => (key in params ? String(params[key]) : match)) : template;
}

// Keys come from the HTML source, where "&" is written "&amp;"; the text node holds the decoded "&".
function translateMarkupText(text) {
    if (!text.includes('&')) return t(text);
    const decode = document.createElement('textarea');
    decode.innerHTML = t(text.replace(/&/g, '&amp;'));
    return decode.value;
}

/**
 * Translates the fixed markup of a page once: text inside elements marked data-i18n, and the
 * title, aria-label and placeholder of elements marked data-i18n-attr. Icons next to the text stay.
 */
export function translateStatic(root) {
    root.querySelectorAll('[data-i18n]').forEach((element) => {
        element.childNodes.forEach((node) => {
            const text = node.nodeType === Node.TEXT_NODE ? node.textContent.trim() : '';
            if (text) node.textContent = node.textContent.replace(text, translateMarkupText(text));
        });
    });
    root.querySelectorAll('[data-i18n-attr]').forEach((element) => {
        ['title', 'aria-label', 'placeholder'].forEach((name) => {
            if (element.hasAttribute(name)) element.setAttribute(name, t(element.getAttribute(name)));
        });
    });
}
