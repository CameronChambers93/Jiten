<script setup lang="ts">
  import { ankiConnect } from '~/composables/useAnkiConnect';
  import {
    allowedActions,
    autoSentencesOf,
    KNOWN_WORDS_OPTIONS,
    matchPreset,
    ROLE_OPTIONS,
    SENTENCE_LIST_OPTIONS,
    setupQuery,
    type AnkiNoteTypeSetup,
    type AutoSentences,
    type FieldRole,
  } from '~/utils/ankiSetups';
  import { useToast } from 'primevue/usetoast';
  import { useConfirm } from 'primevue/useconfirm';

  const props = defineProps<{ setup: AnkiNoteTypeSetup }>();

  const anki = useAnkiSetups();
  const toast = useToast();
  const confirm = useConfirm();

  const draft = ref<AnkiNoteTypeSetup>(structuredClone(toRaw(props.setup)));
  const fieldOrder = ref<string[]>([]);
  const showRoles = ref(false);
  const saving = ref(false);
  const error = ref<string | null>(null);
  const counts = ref<{ deck: string; cards: number }[] | null>(null);
  const countError = ref<string | null>(null);
  const counting = ref(false);

  watch(
    () => props.setup,
    (s) => (draft.value = structuredClone(toRaw(s)))
  );

  const roles = computed(() => draft.value.fieldRoles as Record<string, FieldRole | ''>);
  const allowed = computed(() => allowedActions(roles.value));
  const dirty = computed(() => JSON.stringify(draft.value) !== JSON.stringify(props.setup));
  const autoSentences = computed<AutoSentences>({
    get: () => autoSentencesOf(draft.value),
    set: (v) => (draft.value.autoSentences = v),
  });
  /** The saved setup has automatic sentences as the draft does, so the preview counts what's shown. */
  const autoSaved = computed(() => JSON.stringify(autoSentencesOf(props.setup)) === JSON.stringify(autoSentences.value));
  const preset = computed(() => (fieldOrder.value.length ? matchPreset(draft.value.modelName, fieldOrder.value) : null));

  /** The fields in Anki's order, with any role on a field Anki no longer has listed after them. */
  const fieldRows = computed(() => {
    const names = [...fieldOrder.value];
    for (const name of Object.keys(draft.value.fieldRoles)) if (!names.includes(name)) names.push(name);
    return names.map((name) => ({ name, missing: fieldOrder.value.length > 0 && !fieldOrder.value.includes(name) }));
  });

  async function loadFields() {
    try {
      fieldOrder.value = await ankiConnect<string[]>('modelFieldNames', { modelName: draft.value.modelName });
    } catch {}
  }

  /** Cards the note type and filter match, per deck. */
  async function count() {
    counting.value = true;
    countError.value = null;
    try {
      const cards = await ankiConnect<number[]>('findCards', { query: setupQuery(draft.value) });
      const byDeck = cards.length ? await ankiConnect<Record<string, number[]>>('getDecks', { cards }) : {};
      counts.value = Object.entries(byDeck)
        .map(([deck, ids]) => ({ deck, cards: ids.length }))
        .sort((a, b) => b.cards - a.cards);
    } catch (e) {
      counts.value = null;
      countError.value = extractApiError(e, "Couldn't search Anki");
    } finally {
      counting.value = false;
    }
  }

  onMounted(() => {
    loadFields();
    count();
  });

  function reset() {
    draft.value = structuredClone(toRaw(props.setup));
  }

  function setRole(field: string, role: FieldRole | '') {
    const next = Object.fromEntries(Object.entries(draft.value.fieldRoles).filter(([name]) => name !== field));
    draft.value.fieldRoles = role ? { ...next, [field]: role } : next;
  }

  function applyPreset() {
    if (!preset.value) return;
    draft.value.fieldRoles = { ...preset.value.roles };
    draft.value.preset = preset.value.name;
  }

  // Actions the roles no longer allow are turned off, so the setup always saves.
  watch(allowed, (a) => {
    if (!a.knownWords) draft.value.knownWords = null;
    if (!a.importSentences) draft.value.importSentences = false;
    if (!a.updateMainFields) draft.value.updateMainFields = false;
    if (!a.sentenceList) draft.value.sentenceList = 'none';
  });

  // Automatic sentences reach the card through its sentence list.
  watch(
    () => draft.value.sentenceList,
    (list) => {
      if (list === 'none' && draft.value.autoSentences?.enabled) draft.value.autoSentences = { ...draft.value.autoSentences, enabled: false };
    }
  );

  async function save() {
    saving.value = true;
    error.value = null;
    try {
      await anki.save(draft.value);
      toast.add({ severity: 'success', summary: `${draft.value.modelName} saved`, life: 2500 });
      count();
    } catch (e) {
      error.value = extractApiError(e, "Couldn't save");
    } finally {
      saving.value = false;
    }
  }

  function remove() {
    confirm.require({
      header: `Stop syncing ${props.setup.modelName}`,
      message:
        'Sync will leave its notes alone. Words it made known stay known, and its sentences stay in Jiten. The rotation script stays in its templates until you remove it there.',
      acceptProps: { label: 'Stop syncing', severity: 'danger' },
      rejectProps: { label: 'Cancel', severity: 'secondary', outlined: true },
      accept: async () => {
        try {
          await anki.remove(props.setup.modelName);
        } catch (e) {
          toast.add({ severity: 'error', summary: "Couldn't remove it", detail: extractApiError(e, 'Failed'), life: 5000 });
        }
      },
    });
  }
</script>

<template>
  <Card class="shadow-sm" :data-testid="`setup-${setup.modelName}`">
    <template #title>
      <div class="flex flex-wrap items-center justify-between gap-2">
        <span>{{ setup.modelName }}</span>
        <span class="flex items-center gap-2">
          <Tag v-if="draft.preset" :value="draft.preset" severity="secondary" />
          <Button icon="pi pi-trash" severity="danger" text rounded size="small" aria-label="Stop syncing" @click="remove" />
        </span>
      </div>
    </template>
    <template #content>
      <div class="flex flex-col gap-4">
        <div class="flex flex-col gap-1">
          <label :for="`filter-${setup.modelName}`" class="text-sm text-gray-500 dark:text-gray-400">Filter (an Anki search; leave empty for every note)</label>
          <div class="flex gap-2">
            <InputText
              :id="`filter-${setup.modelName}`"
              v-model="draft.filter"
              class="flex-1 font-mono text-xs"
              placeholder="For example: -deck:Examples"
              spellcheck="false"
              @keydown.enter="count"
            />
            <Button label="Count" icon="pi pi-search" size="small" severity="secondary" outlined :loading="counting" @click="count" />
          </div>
          <div class="text-xs text-gray-500 dark:text-gray-400">
            <template v-if="countError">{{ countError }}</template>
            <template v-else-if="counts && !counts.length">No cards match.</template>
            <template v-else-if="counts">
              <span v-for="(c, i) in counts.slice(0, 8)" :key="c.deck"
                >{{ i ? ' · ' : '' }}{{ c.deck }} <b>{{ c.cards.toLocaleString() }}</b></span
              ><span v-if="counts.length > 8"> · {{ counts.length - 8 }} more decks</span>
            </template>
          </div>
        </div>

        <div class="grid grid-cols-1 gap-3 md:grid-cols-2">
          <div class="flex flex-col gap-2">
            <div class="text-sm font-semibold">Known words</div>
            <Select
              v-model="draft.knownWords"
              :options="KNOWN_WORDS_OPTIONS"
              option-label="label"
              option-value="value"
              size="small"
              :disabled="!allowed.knownWords"
              :aria-label="`Known words from ${setup.modelName}`"
            />
            <small class="text-xs text-gray-500 dark:text-gray-400">
              <template v-if="!allowed.knownWords">Map a field to Word first.</template>
              <template v-else>A word on a card at this state becomes known in Jiten, with its review history. It stays known if the note goes.</template>
            </small>
          </div>
          <div class="flex flex-col gap-2">
            <div class="text-sm font-semibold">Sentences</div>
            <label class="flex items-center gap-2 text-sm" :class="{ 'opacity-50': !allowed.importSentences }">
              <Checkbox v-model="draft.importSentences" binary :disabled="!allowed.importSentences" /> Import each note's sentence
            </label>
            <label class="flex items-center gap-2 text-sm">
              <Checkbox v-model="draft.takeFlags" binary />
              <span>Take in flags (<code>sentence-fix::</code> and <code>jiten-flag</code> tags)</span>
            </label>
            <label class="flex items-center gap-2 text-sm"> <Checkbox v-model="draft.foldDuplicates" binary /> List words on several notes to fold </label>
            <label class="flex items-center gap-2 text-sm" :class="{ 'opacity-50': !allowed.updateMainFields }">
              <Checkbox v-model="draft.updateMainFields" binary :disabled="!allowed.updateMainFields" /> Update the main sentence fields when the main sentence
              changes
            </label>
          </div>
        </div>

        <div class="flex flex-col gap-1">
          <div class="text-sm font-semibold">Sentence list</div>
          <div class="flex flex-col gap-1">
            <label
              v-for="o in SENTENCE_LIST_OPTIONS"
              :key="o.value"
              class="flex items-start gap-2 text-sm"
              :class="{ 'opacity-50': o.value !== 'none' && !allowed.sentenceList }"
            >
              <RadioButton v-model="draft.sentenceList" :value="o.value" :disabled="o.value !== 'none' && !allowed.sentenceList" class="mt-0.5" />
              <span
                ><b>{{ o.label }}</b> <span class="text-gray-500 dark:text-gray-400">· {{ o.detail }}</span></span
              >
            </label>
          </div>
          <small v-if="!allowed.sentenceList" class="text-xs text-gray-500 dark:text-gray-400">Give a field the Sentence list role to write a list.</small>
        </div>

        <AnkiRotationScriptPanel v-if="draft.sentenceList === 'script' && allowed.sentenceList" :setup="setup" :field-order="fieldOrder" />

        <AnkiAutoSentencesPanel
          v-if="draft.sentenceList !== 'none' && allowed.sentenceList"
          v-model="autoSentences"
          :model-name="setup.modelName"
          :saved="autoSaved"
        />

        <div>
          <Button
            :label="showRoles ? 'Hide field roles' : `Field roles (${Object.keys(draft.fieldRoles).length})`"
            :icon="showRoles ? 'pi pi-chevron-up' : 'pi pi-chevron-down'"
            size="small"
            text
            @click="showRoles = !showRoles"
          />
          <div v-if="showRoles" class="mt-2 flex flex-col gap-2">
            <div v-if="preset && preset.name !== draft.preset" class="flex items-center gap-2 text-sm">
              <span>The {{ preset.name }} preset fits this note type.</span>
              <Button label="Use it" size="small" outlined @click="applyPreset" />
            </div>
            <div v-for="row in fieldRows" :key="row.name" class="grid grid-cols-[minmax(0,1fr)_minmax(0,1.4fr)] items-center gap-3">
              <label :for="`role-${setup.modelName}-${row.name}`" class="truncate text-sm" :class="{ 'text-red-500': row.missing }"
                >{{ row.name }}<template v-if="row.missing"> (not in Anki)</template></label
              >
              <Select
                :model-value="draft.fieldRoles[row.name] ?? ''"
                :input-id="`role-${setup.modelName}-${row.name}`"
                :options="ROLE_OPTIONS"
                option-label="label"
                option-value="value"
                size="small"
                @update:model-value="(v: FieldRole | '') => setRole(row.name, v)"
              />
            </div>
          </div>
        </div>

        <div class="flex items-center justify-end gap-2">
          <small v-if="error" class="text-red-500">{{ error }}</small>
          <Button label="Reset" severity="secondary" text size="small" :disabled="!dirty" @click="reset" />
          <Button label="Save" icon="pi pi-check" size="small" :disabled="!dirty" :loading="saving" @click="save" />
        </div>
      </div>
    </template>
  </Card>
</template>
