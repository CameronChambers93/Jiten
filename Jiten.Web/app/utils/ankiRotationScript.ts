import { fieldsFor, type FieldRole } from '~/utils/ankiSetups';

/**
 * The rotation script: a marked block Jiten inserts into a note type's front and back templates, so a card shows a
 * different one of its word's sentences on each review, and its back has a flag button. The block reads the sentence
 * list Sync writes, and fills the template's slots (`data-jiten="…"`). Templates without slots get their sentence,
 * English, audio and picture field references wrapped in slots, which removing the block unwraps again.
 *
 * Three things Anki does shape the script: it re-runs card scripts in one page (so no top-level const/let), it
 * rewrites every src="…" in the card's HTML, scripts included (so the script never writes one), and it fills field
 * references anywhere in a template, comments in scripts included (so the script never writes double braces).
 */
export const SCRIPT_VERSION = 1;

const START = /<!-- jiten:rotation v(\d+) -->/;
const BLOCK = /<!-- jiten:rotation v\d+ -->[\s\S]*?<!-- \/jiten:rotation -->\n?/g;

export type Side = 'front' | 'back';

/** The slot a field role fills: the sentence with furigana (Lapis+'s `sentence`), without, the English, audio, picture. */
const SLOT_OF: Partial<Record<FieldRole, string>> = {
  sentenceFurigana: 'sentence',
  sentence: 'sentence-text',
  sentencePlain: 'sentence-text',
  sentenceEnglish: 'english',
  sentenceAudio: 'audio',
  picture: 'picture',
};

export interface Slot {
  role: FieldRole;
  field: string;
  slot: string;
  /** The field reference as written in the template, such as `{{furigana:SentenceFurigana}}`. */
  ref: string;
}

function escapeRegex(s: string): string {
  return s.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
}

/** A reference that shows the field, with any filters (`{{furigana:Sentence}}`), but not a section tag. */
function fieldRef(field: string): RegExp {
  return new RegExp(`\\{\\{(?:[\\w-]+:)*${escapeRegex(field)}\\}\\}`, 'g');
}

/** Slots written into the template by hand (Lapis+), as opposed to Jiten's wrapped ones. */
export function handMadeSlots(template: string): string[] {
  return [...template.matchAll(/data-jiten="([\w-]+)"(?! data-jiten-slot)/g)].map((m) => m[1]!);
}

/** The field references Jiten would wrap in slots, in template order; none when the template has its own slots. */
export function findSlots(template: string, roles: Record<string, FieldRole | ''>): Slot[] {
  if (handMadeSlots(removeRotation(template)).length) return [];
  const clean = removeRotation(template);
  const slots: (Slot & { at: number })[] = [];
  for (const [role, slot] of Object.entries(SLOT_OF) as [FieldRole, string][]) {
    for (const field of fieldsFor(roles, role)) {
      for (const m of clean.matchAll(fieldRef(field))) slots.push({ role, field, slot, ref: m[0], at: m.index! });
    }
  }
  return slots.sort((a, b) => a.at - b.at).map(({ at: _at, ...s }) => s);
}

/** The template with the block and wrapped slots taken out, as it was before Jiten inserted them. */
export function removeRotation(template: string): string {
  return template
    .replace(BLOCK, '')
    .replace(/\{\{\^([^}]+)\}\}(\{\{[^}]+\}\})\{\{\/\1\}\}\{\{#\1\}\}<span data-jiten="[\w-]+" data-jiten-slot><\/span>\{\{\/\1\}\}/g, '$2');
}

/** The script version in the template, or null when it has none. */
export function installedVersion(template: string): number | null {
  const m = template.match(START);
  return m ? Number(m[1]) : null;
}

/**
 * The template with the block (re)inserted at its start and, when it has no slots of its own, its field references
 * wrapped in slots that show the original field on notes without a sentence list.
 */
export function insertRotation(template: string, roles: Record<string, FieldRole | ''>, listField: string, side: Side): string {
  let out = removeRotation(template);
  const own = handMadeSlots(out);
  for (const s of findSlots(out, roles)) {
    out = out.replace(
      new RegExp(`(?<!\\{\\{\\^${escapeRegex(listField)}\\}\\})${escapeRegex(s.ref)}`, 'g'),
      `{{^${listField}}}${s.ref}{{/${listField}}}{{#${listField}}}<span data-jiten="${s.slot}" data-jiten-slot></span>{{/${listField}}}`
    );
  }
  const meta = side === 'back' && !own.includes('meta');
  // First, so the template's own scripts (Lapis+ colours the target word) run on the picked sentence.
  return `${rotationBlock(listField, side, meta)}\n${out}`;
}

/** The block for one side. The back adds a source line with the flag button unless the template has its own. */
export function rotationBlock(listField: string, side: Side, meta: boolean): string {
  return `<!-- jiten:rotation v${SCRIPT_VERSION} -->
{{#${listField}}}<div class="jiten-sentences-data" hidden>{{${listField}}}</div>${side === 'back' ? '<div id="jiten-back" hidden></div>' : ''}${meta ? '<div class="jiten-sentence-meta" data-jiten="meta"></div>' : ''}{{/${listField}}}
<script>
${ROTATION_SCRIPT}
jitenRotation(${side === 'back'});
</script>
<!-- /jiten:rotation -->`;
}

/** Shared by both sides. Function declarations and var only: Anki re-runs card scripts in one page. */
const ROTATION_SCRIPT = `// Jiten: a different sentence of the word on every review, and a flag button on the back.
var JITEN_PLAY_SVG = '<svg class="playImage" viewBox="0 0 64 64" version="1.1"><circle cx="32" cy="32" r="29"></circle><path d="M56.502,32.301l-37.502,20.101l0.329,-40.804l37.173,20.703Z"></path></svg>';

function jitenRuby(html) {
    return html.replace(/ ?([^ >]+?)\\[(.+?)\\]/g, "<ruby><rb>$1</rb><rt>$2</rt></ruby>");
}

function jitenPick(onBack) {
    var list = document.querySelector(".jiten-sentences-data .jiten-sentences");
    if (!list) return null;
    var items = Array.prototype.slice.call(list.querySelectorAll(".jiten-sentence"));
    if (items.length === 0) return null;
    var note = list.getAttribute("data-note");
    // The back reuses the front's pick: the reviewer keeps one page (window); sessionStorage covers apps that don't
    var picked = null;
    if (onBack) {
        picked = window.jitenPicked;
        if (!picked) { try { picked = JSON.parse(sessionStorage.getItem("jiten-picked")); } catch (e) {} }
    }
    var item = picked && picked.note === note && items.find(function (i) { return i.getAttribute("data-id") === picked.id; });
    if (!item) item = items[Math.floor(Math.random() * items.length)];
    window.jitenPicked = { note: note, id: item.getAttribute("data-id") };
    try { sessionStorage.setItem("jiten-picked", JSON.stringify(window.jitenPicked)); } catch (e) {}
    return { note: note, item: item, index: items.indexOf(item), count: items.length };
}

function jitenFlag(link, note, id) {
    link.textContent = "⚑ Flagging…";
    fetch("http://127.0.0.1:8765", {
        method: "POST",
        body: JSON.stringify({ action: "addTags", version: 6, params: { notes: [Number(note)], tags: "sentence-fix::" + id } }),
    })
        .then(function (r) { return r.json(); })
        .then(function (r) {
            if (r.error) throw new Error(r.error);
            document.querySelectorAll(".jiten-flag").forEach(function (l) { l.textContent = "⚑ Flagged"; l.classList.add("flagged"); });
        })
        .catch(function (e) { link.textContent = "⚑ Couldn't flag (" + e.message + ")"; });
}

function jitenFill(root, name, html) {
    root.querySelectorAll('[data-jiten="' + name + '"]').forEach(function (el) { el.innerHTML = html; });
}

function jitenRotation(onBack) {
    // The front's script also runs on the back when the back includes the front side; the back's own run does the work
    if (!onBack && document.getElementById("jiten-back")) return;
    var pick = jitenPick(onBack);
    if (!pick) return;
    var part = function (name) { return pick.item.querySelector(".jiten-sentence-" + name); };
    var plain = part("text") ? part("text").innerHTML : "";
    var withReadings = jitenRuby((part("furigana") || part("text") || { innerHTML: "" }).innerHTML);
    var english = part("english") ? part("english").innerHTML : "";
    var picture = part("picture");
    var audio = part("audio");

    var fill = function (root) {
        jitenFill(root, "sentence", withReadings);
        jitenFill(root, "sentence-text", plain);
        jitenFill(root, "english", english);
    };
    fill(document);
    var clickTemplate = document.getElementById("click-sentence");
    if (clickTemplate) fill(clickTemplate.content);

    document.querySelectorAll('[data-jiten="picture-box"]').forEach(function (box) { box.style.display = picture ? "" : "none"; });
    document.querySelectorAll('[data-jiten="picture"]').forEach(function (slot) {
        if (!picture) { slot.replaceChildren(); return; }
        // Built with the DOM, never as an HTML string: Anki rewrites src attributes it finds in the card's text
        var img = document.createElement("img");
        img.alt = "";
        img.src = picture.getAttribute("src");
        slot.replaceChildren(img);
    });

    document.querySelectorAll('[data-jiten="audio-box"]').forEach(function (box) { box.style.display = audio ? "" : "none"; });
    document.querySelectorAll('[data-jiten="audio"]').forEach(function (slot) {
        if (!audio) { slot.replaceChildren(); return; }
        var button = document.createElement("a");
        button.className = "replay-button soundLink jiten-play";
        button.href = "#";
        button.innerHTML = JITEN_PLAY_SVG;
        button.addEventListener("click", function (e) { e.preventDefault(); new Audio(audio.getAttribute("src")).play(); });
        slot.replaceChildren(button);
    });

    if (!onBack) return;
    var source = pick.item.getAttribute("data-source");
    document.querySelectorAll('[data-jiten="meta"]').forEach(function (meta) {
        var label = document.createElement("span");
        label.textContent = (source ? source + " · " : "") + (pick.index + 1) + "/" + pick.count;
        var flag = document.createElement("a");
        flag.href = "#";
        flag.className = "jiten-flag tappable";
        flag.textContent = "⚑ Flag";
        flag.addEventListener("click", function (e) { e.preventDefault(); jitenFlag(flag, pick.note, pick.item.getAttribute("data-id")); });
        meta.replaceChildren(label, " ", flag);
    });
}`;
