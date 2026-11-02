import type { MiningFieldSource } from '~/utils/miningCardFields';
import { ankiConnect } from '~/composables/useAnkiConnect';
import {
  DEFAULT_CARD_TARGETS,
  hasSentenceActions,
  newSetup,
  rolesFromMapping,
  suggestRoles,
  withRoles,
  type AnkiNoteTypeSetup,
  type CardTargets,
} from '~/utils/ankiSetups';

/** The per-browser card settings before they moved to the server; read once to upload them. */
const LEGACY_KEY = 'jiten-mining-cards';

interface LegacySettings {
  version: 1;
  deckName: string;
  modelName: string;
  fieldsByModel: Record<string, Record<string, MiningFieldSource>>;
  tags: string;
  padStartMs: number;
  padEndMs: number;
  ankiKey: string;
}

// Shared by every page that mines or syncs.
const setups = ref<AnkiNoteTypeSetup[]>([]);
const cardTargets = ref<CardTargets>(structuredClone(DEFAULT_CARD_TARGETS));
const knownWordsSyncedAt = ref<string | null>(null);
const loaded = ref(false);
let loading: Promise<void> | null = null;

function readLegacy(): LegacySettings | null {
  try {
    const raw = localStorage.getItem(LEGACY_KEY);
    const blob = raw ? (JSON.parse(raw) as LegacySettings) : null;
    return blob?.version === 1 ? blob : null;
  } catch {
    return null;
  }
}

/** The note type setups and new-card target, kept on the server so every device uses the same ones. */
export function useAnkiSetups() {
  const { $api } = useNuxtApp();

  async function fetchAll() {
    // The targets first: reading an old target moves its empty-field policies to a setup, which the list must include.
    const target = await $api<CardTargets>('anki-sync/card-target');
    const list = await $api<{ setups: AnkiNoteTypeSetup[]; knownWordsSyncedAt: string | null }>('anki-sync/setups');
    setups.value = list.setups;
    knownWordsSyncedAt.value = list.knownWordsSyncedAt;
    cardTargets.value = target?.targets?.length ? target : structuredClone(DEFAULT_CARD_TARGETS);
  }

  /**
   * Uploads this browser's old card settings when the server has none: the deck and note type become the card target,
   * and each note type's field mapping becomes its setup's roles (with a preset's other roles where it fits).
   */
  async function migrateLegacy() {
    if (!import.meta.client) return;
    const legacy = readLegacy();
    if (!legacy) return;
    const configured = cardTargets.value.targets.some((t) => t.deckName);
    if (!configured && legacy.deckName) {
      const { deckName, modelName, tags, padStartMs, padEndMs } = legacy;
      await saveCardTargets({ targets: [{ deckName, modelName, tags, when: null }], padStartMs, padEndMs });
    }
    let failed = false;
    for (const [modelName, mapping] of Object.entries(legacy.fieldsByModel ?? {})) {
      if (setups.value.some((s) => s.modelName === modelName)) continue;
      let fieldNames: string[] = Object.keys(mapping);
      try {
        fieldNames = await ankiConnect<string[]>('modelFieldNames', { modelName }, legacy.ankiKey);
      } catch {}
      // The mapping the user chose wins; the preset fills only roles the old mapping had no source for (the sentence list,
      // URL, previous line), which it left blank. Actions the roles can't support are off.
      const suggested = suggestRoles(modelName, fieldNames).roles;
      const chosen = rolesFromMapping(mapping);
      const roles = { ...chosen };
      for (const [field, role] of Object.entries(suggested)) {
        if (!(field in roles) && ['sentenceList', 'url', 'previousLine', 'frequency'].includes(role)) roles[field] = role;
      }
      await save(withRoles(newSetup(modelName, fieldNames), roles)).catch(() => (failed = true));
    }
    useAnkiDevice().setKey(legacy.ankiKey ?? '');
    // Kept when a setup couldn't be saved, so the next load tries again.
    if (failed) return;
    try {
      localStorage.removeItem(LEGACY_KEY);
    } catch {}
  }

  async function load(force = false) {
    if (loaded.value && !force) return;
    loading ??= (async () => {
      try {
        await fetchAll();
        await migrateLegacy();
        loaded.value = true;
      } finally {
        loading = null;
      }
    })();
    await loading;
  }

  async function save(setup: AnkiNoteTypeSetup): Promise<AnkiNoteTypeSetup> {
    const { setup: saved, knownWordsSyncedAt: syncedAt } = await $api<{ setup: AnkiNoteTypeSetup; knownWordsSyncedAt: string | null }>('anki-sync/setups', {
      method: 'PUT',
      body: setup,
    });
    const i = setups.value.findIndex((s) => s.modelName === saved.modelName);
    if (i >= 0) setups.value.splice(i, 1, saved);
    else setups.value = [...setups.value, saved].sort((a, b) => a.modelName.localeCompare(b.modelName));
    // Changing which notes or cards count resets when known words were last sent.
    knownWordsSyncedAt.value = syncedAt;
    return saved;
  }

  async function remove(modelName: string) {
    await $api('anki-sync/setups', { method: 'DELETE', query: { modelName } });
    setups.value = setups.value.filter((s) => s.modelName !== modelName);
  }

  async function saveCardTargets(targets: CardTargets) {
    await $api('anki-sync/card-target', { method: 'PUT', body: targets });
    // The last target takes everything else, so it has no rule.
    cardTargets.value = { ...targets, targets: targets.targets.map((t, i, all) => (i === all.length - 1 ? { ...t, when: null } : t)) };
  }

  /**
   * Records when Sync last sent known words. `previous` is the value Sync started from; if a setup reset it meanwhile the
   * server keeps the reset, so the next Sync sends every card.
   */
  async function setKnownWordsSynced(syncedAt: Date, previous: string | null) {
    try {
      await $api('anki-sync/known-words-synced', { method: 'PUT', body: { syncedAt: syncedAt.toISOString(), previous } });
      knownWordsSyncedAt.value = syncedAt.toISOString();
    } catch {
      await fetchAll().catch(() => {});
    }
  }

  function setupFor(modelName: string): AnkiNoteTypeSetup | null {
    return setups.value.find((s) => s.modelName === modelName) ?? null;
  }

  /** Setups whose notes are mining notes (any sentence action on). */
  const miningSetups = computed(() => setups.value.filter(hasSentenceActions));

  return {
    setups: readonly(setups),
    cardTargets: readonly(cardTargets),
    knownWordsSyncedAt: readonly(knownWordsSyncedAt),
    loaded: readonly(loaded),
    miningSetups,
    load,
    save,
    remove,
    saveCardTargets,
    setKnownWordsSynced,
    setupFor,
  };
}
