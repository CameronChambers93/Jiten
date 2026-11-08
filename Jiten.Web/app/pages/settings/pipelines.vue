<script setup lang="ts">
  import type { DeepReadonly } from 'vue';
  import { useToast } from 'primevue/usetoast';
  import { ankiConnect } from '~/composables/useAnkiConnect';
  import { ankiSearchValue, hasSentenceActions, setupQuery } from '~/utils/ankiSetups';
  import { getMediaTypeText } from '~/utils/mediaTypeMapper';
  import {
    conditionChips,
    ENGLISH_LABELS,
    HAS_CARD_LABELS,
    newStep,
    NO_CARD_LABELS,
    PIPELINE_PAGES,
    stepSummary,
    type AnkiStep,
    type MiningPipelines,
    type Pipeline,
    type PipelineStep,
  } from '~/utils/pipelines';

  definePageMeta({ middleware: ['auth'] });
  useHead({ title: 'Pipelines - Settings - Jiten' });

  const toast = useToast();
  const store = usePipelines();
  const anki = useAnkiSetups();
  const { device } = useAnkiDevice();

  /** The pipelines being edited: a copy, saved as a whole. */
  const draft = ref<MiningPipelines | null>(null);
  const initial = ref('');
  const loadError = ref<string | null>(null);
  const saving = ref(false);
  const saveError = ref<string | null>(null);
  /** Open steps, as "pipelineIndex:stepIndex". */
  const open = ref<Set<string>>(new Set());

  const decks = ref<string[]>([]);
  const models = ref<string[]>([]);
  const ankiLoading = ref(false);
  const ankiError = ref<string | null>(null);

  const dirty = computed(() => !!draft.value && JSON.stringify(draft.value) !== initial.value);
  const noteTypes = computed(() => [...new Set([...anki.miningSetups.value.map((s) => s.modelName), ...models.value])]);

  const ENGLISH_OPTIONS = Object.entries(ENGLISH_LABELS).map(([value, label]) => ({ value, label }));
  const HAS_CARD_OPTIONS = Object.entries(HAS_CARD_LABELS).map(([value, label]) => ({ value, label }));
  const NO_CARD_OPTIONS = Object.entries(NO_CARD_LABELS).map(([value, label]) => ({ value, label }));
  const KIND_LABELS: Record<PipelineStep['kind'], string> = { build: 'Build', anki: 'Anki', tags: 'Tags' };

  function reset(from: MiningPipelines | DeepReadonly<MiningPipelines>) {
    draft.value = structuredClone(toRaw(from)) as MiningPipelines;
    initial.value = JSON.stringify(draft.value);
    open.value = new Set();
  }

  async function load() {
    loadError.value = null;
    try {
      reset(await store.load(true));
    } catch (e) {
      loadError.value = extractApiError(e, "Couldn't load your pipelines");
    }
  }

  async function connect() {
    ankiLoading.value = true;
    ankiError.value = null;
    try {
      [decks.value, models.value] = await Promise.all([ankiConnect<string[]>('deckNames', {}), ankiConnect<string[]>('modelNames', {})]);
      decks.value.sort();
      models.value.sort();
    } catch (e) {
      ankiError.value = e instanceof TypeError ? 'unreachable' : String((e as Error).message ?? e);
    } finally {
      ankiLoading.value = false;
    }
  }

  onMounted(() => {
    load();
    anki.load().catch(() => {});
    connect();
  });

  function toggle(key: string) {
    const next = new Set(open.value);
    if (!next.delete(key)) next.add(key);
    open.value = next;
  }

  function newId() {
    return `p${Date.now().toString(36)}${Math.random().toString(36).slice(2, 6)}`;
  }

  function addPipeline() {
    draft.value!.pipelines.push({ id: newId(), name: 'New pipeline', steps: [] });
  }

  function duplicate(i: number) {
    const p = draft.value!.pipelines[i]!;
    draft.value!.pipelines.splice(i + 1, 0, { ...structuredClone(toRaw(p)), id: newId(), name: `${p.name} copy`.slice(0, 60) });
  }

  function removePipeline(i: number) {
    const d = draft.value!;
    const [gone] = d.pipelines.splice(i, 1);
    // A page whose default went falls back to the first pipeline.
    for (const page of PIPELINE_PAGES) if (d.defaults[page.key] === gone?.id) d.defaults[page.key] = d.pipelines[0]!.id;
    open.value = new Set();
  }

  function addStep(p: Pipeline, kind: PipelineStep['kind'], pi: number) {
    p.steps.push(newStep(kind));
    toggle(`${pi}:${p.steps.length - 1}`);
  }

  function moveStep(p: Pipeline, i: number, by: -1 | 1) {
    const j = i + by;
    if (j < 0 || j >= p.steps.length) return;
    [p.steps[i], p.steps[j]] = [p.steps[j]!, p.steps[i]!];
    open.value = new Set();
  }

  function removeStep(p: Pipeline, i: number) {
    p.steps.splice(i, 1);
    open.value = new Set();
  }

  /** "Default on every page", or the pages it's the default on. Empty when none. */
  function defaultLabel(p: Pipeline): string {
    const pages = PIPELINE_PAGES.filter((page) => draft.value?.defaults[page.key] === p.id).map((page) => page.label);
    if (!pages.length) return '';
    return pages.length === PIPELINE_PAGES.length ? 'Default on every page' : `Default on ${pages.join(', ')}`;
  }

  function needsTargets(s: AnkiStep) {
    return s.noCard === 'targets' || s.hasCard === 'ask' || s.hasCard === 'new';
  }

  /** Per deck and note type: the note type's Sync filter leaves out the deck, so its new cards wouldn't sync. */
  const filterWarnings = ref<Record<string, string>>({});
  const filterChecked = new Set<string>();

  async function checkFilter(deckName: string, modelName: string) {
    const key = `${deckName}\u0000${modelName}`;
    const setup = anki.setupFor(modelName);
    if (filterChecked.has(key) || !setup?.filter) return;
    filterChecked.add(key);
    try {
      const deck = `"deck:${ankiSearchValue(deckName)}"`;
      const [all, kept] = await Promise.all([
        ankiConnect<number[]>('findNotes', { query: setupQuery({ modelName, filter: null }, deck) }),
        ankiConnect<number[]>('findNotes', { query: setupQuery(setup, deck) }),
      ]);
      if (all.length > 0 && kept.length === 0) filterWarnings.value = { ...filterWarnings.value, [key]: `The ${modelName} filter leaves out ${deckName}, so new cards won't sync.` };
    } catch {
      filterChecked.delete(key);
    }
  }

  watch(
    () =>
      [models.value.length, anki.loaded.value, (draft.value?.pipelines ?? []).flatMap((p) => p.steps.flatMap((s) => (s.kind === 'anki' ? s.targets.map((t) => `${t.deckName}\u0000${t.modelName}`) : [])))] as const,
    ([connected, loaded, keys]) => {
      if (!connected || !loaded) return;
      for (const key of new Set(keys)) {
        const [deck, model] = key.split('\u0000');
        if (deck && model) checkFilter(deck, model);
      }
    }
  );

  /** Per target: why a new note there won't work or won't sync, from the note type's setup. */
  function targetWarnings(s: AnkiStep): Record<number, string> {
    const out: Record<number, string> = {};
    s.targets.forEach((t, i) => {
      if (!t.modelName) return;
      const setup = anki.setupFor(t.modelName);
      if (!setup || !hasSentenceActions(setup)) out[i] = `${t.modelName} has no field roles in Anki settings, so new cards can't be made or synced.`;
      else if (filterWarnings.value[`${t.deckName}\u0000${t.modelName}`]) out[i] = filterWarnings.value[`${t.deckName}\u0000${t.modelName}`]!;
    });
    return out;
  }

  /** The first thing that stops a save, as the server would say it. */
  const problem = computed(() => {
    for (const p of draft.value?.pipelines ?? []) {
      if (!p.name.trim()) return 'Every pipeline needs a name.';
      for (const [i, s] of p.steps.entries()) {
        if (s.kind !== 'anki' || !needsTargets(s)) continue;
        const at = `${p.name}, step ${i + 1}`;
        if (!s.targets.length) return `${at}: add a target for new cards.`;
        for (const [j, t] of s.targets.entries()) {
          if (!t.deckName || !t.modelName) return `${at}: target ${j + 1} needs a deck and a note type.`;
          if (j < s.targets.length - 1 && (!t.when || (!t.when.mediaTypes.length && !t.when.titles.length)))
            return `${at}: target ${j + 1} needs a media type or a title.`;
        }
      }
    }
    return null;
  });

  async function save() {
    if (!draft.value) return;
    saving.value = true;
    saveError.value = null;
    try {
      reset(await store.save(draft.value));
      toast.add({ severity: 'success', summary: 'Pipelines saved', life: 2500, group: 'bottom' });
    } catch (e) {
      saveError.value = extractApiError(e, "Couldn't save the pipelines");
    } finally {
      saving.value = false;
    }
  }

  onBeforeRouteLeave(() => (dirty.value ? window.confirm('Leave without saving your pipeline changes?') : true));

  const mediaTypeName = (t: number) => getMediaTypeText(t);
  const origin = import.meta.client ? window.location.origin : '';
</script>

<template>
  <div class="container mx-auto flex max-w-4xl flex-col gap-4 p-2 md:p-4">
    <div class="flex items-center gap-2">
      <NuxtLink to="/settings">
        <Button icon="pi pi-arrow-left" severity="secondary" text rounded aria-label="Settings" />
      </NuxtLink>
      <h1 class="text-2xl font-bold">Pipelines</h1>
    </div>
    <p class="text-sm text-gray-600 dark:text-gray-300 max-w-prose">
      A pipeline is what the sentence button does. Every pipeline first saves the sentence to the word's list, then runs its steps. Of several matching Anki
      steps, or several matching Build steps, the first runs; every matching Tags step adds its tags. Field roles and empty fields are in
      <NuxtLink to="/settings/anki" class="underline">Anki settings</NuxtLink>, per note type.
    </p>

    <Message v-if="loadError" severity="error" :closable="false">
      {{ loadError }} <Button label="Try again" size="small" severity="secondary" text @click="load" />
    </Message>
    <Message v-if="ankiError" severity="warn" :closable="false">
      <template v-if="ankiError === 'unreachable'">
        Couldn't reach AnkiConnect at {{ device.url }}, so decks and note types can't be listed. Check that Anki is open and that <code>{{ origin }}</code> is in
        AnkiConnect's <code>webCorsOriginList</code>.
      </template>
      <template v-else>AnkiConnect: {{ ankiError }}</template>
      <Button label="Try again" size="small" severity="secondary" text :loading="ankiLoading" @click="connect" />
    </Message>
    <Message v-if="draft && !draft.saved" severity="info" :closable="false">
      These pipelines are built from your card settings. Nothing changes until you save.
    </Message>

    <template v-if="draft">
      <Card v-for="(p, pi) in draft.pipelines" :key="p.id" data-testid="pipeline">
        <template #content>
          <div class="flex flex-col gap-3">
            <div class="flex flex-wrap items-center gap-2">
              <InputText v-model="p.name" :aria-label="`Name of pipeline ${pi + 1}`" class="font-semibold min-w-0 flex-1 sm:flex-none sm:w-64" maxlength="60" />
              <Tag v-if="defaultLabel(p)" severity="secondary" :value="defaultLabel(p)" />
              <span class="flex-1" />
              <Button label="Duplicate" icon="pi pi-copy" size="small" severity="secondary" text @click="duplicate(pi)" />
              <Button
                label="Remove"
                icon="pi pi-trash"
                size="small"
                severity="secondary"
                text
                :disabled="draft.pipelines.length === 1"
                @click="removePipeline(pi)"
              />
            </div>

            <ol v-if="p.steps.length" class="flex flex-col gap-2">
              <li v-for="(s, si) in p.steps" :key="`${p.id}-${si}`" class="rounded border border-surface-200 dark:border-surface-700 bg-surface-50 dark:bg-surface-900">
                <div class="flex flex-wrap items-center gap-2 px-2 py-1.5">
                  <button
                    type="button"
                    class="flex flex-1 min-w-0 flex-wrap items-center gap-2 text-left cursor-pointer"
                    :aria-expanded="open.has(`${pi}:${si}`)"
                    @click="toggle(`${pi}:${si}`)"
                  >
                    <i class="pi text-xs text-gray-400" :class="open.has(`${pi}:${si}`) ? 'pi-chevron-down' : 'pi-chevron-right'" />
                    <span class="text-xs text-gray-400 tabular-nums">{{ si + 1 }}</span>
                    <span class="font-semibold text-sm w-12">{{ KIND_LABELS[s.kind] }}</span>
                    <span class="text-sm text-gray-600 dark:text-gray-300 min-w-0">{{ stepSummary(s) }}</span>
                    <Tag v-for="chip in conditionChips(s.when, mediaTypeName)" :key="chip" :value="chip" class="!text-xs" />
                  </button>
                  <div class="flex">
                    <Button icon="pi pi-arrow-up" size="small" severity="secondary" text :disabled="si === 0" aria-label="Move step up" @click="moveStep(p, si, -1)" />
                    <Button
                      icon="pi pi-arrow-down"
                      size="small"
                      severity="secondary"
                      text
                      :disabled="si === p.steps.length - 1"
                      aria-label="Move step down"
                      @click="moveStep(p, si, 1)"
                    />
                    <Button icon="pi pi-times" size="small" severity="secondary" text aria-label="Remove step" @click="removeStep(p, si)" />
                  </div>
                </div>

                <div v-if="open.has(`${pi}:${si}`)" class="flex flex-col gap-3 border-t border-surface-200 dark:border-surface-700 p-3">
                  <template v-if="s.kind === 'build'">
                    <div class="flex flex-wrap gap-4">
                      <div v-for="part in ['audio', 'picture', 'video'] as const" :key="part" class="flex items-center gap-2">
                        <Checkbox v-model="s[part]" :input-id="`p${pi}s${si}-${part}`" binary />
                        <label :for="`p${pi}s${si}-${part}`" class="text-sm">{{ { audio: 'Audio', picture: 'Screenshot', video: 'Video clip' }[part] }}</label>
                      </div>
                    </div>
                    <div class="flex flex-col gap-1">
                      <span class="text-xs text-gray-500 dark:text-gray-400">English</span>
                      <SelectButton v-model="s.english" :options="ENGLISH_OPTIONS" option-label="label" option-value="value" :allow-empty="false" size="small" aria-label="English" />
                      <small class="text-xs text-gray-500 dark:text-gray-400">The site's translation settings can limit this to subtitles, or turn card English off.</small>
                    </div>
                    <div class="grid grid-cols-1 sm:grid-cols-2 gap-3 sm:max-w-md">
                      <div class="flex flex-col gap-1">
                        <label :for="`p${pi}s${si}-pad-start`" class="text-xs text-gray-500 dark:text-gray-400">Audio lead-in (ms)</label>
                        <InputNumber v-model="s.padStartMs" :input-id="`p${pi}s${si}-pad-start`" :min="0" :max="5000" :step="50" show-buttons fluid size="small" />
                      </div>
                      <div class="flex flex-col gap-1">
                        <label :for="`p${pi}s${si}-pad-end`" class="text-xs text-gray-500 dark:text-gray-400">Audio tail (ms)</label>
                        <InputNumber v-model="s.padEndMs" :input-id="`p${pi}s${si}-pad-end`" :min="0" :max="5000" :step="50" show-buttons fluid size="small" />
                      </div>
                    </div>
                  </template>

                  <template v-else-if="s.kind === 'anki'">
                    <div class="flex flex-col gap-1">
                      <span class="text-xs text-gray-500 dark:text-gray-400">When the word already has a card</span>
                      <SelectButton
                        v-model="s.hasCard"
                        :options="HAS_CARD_OPTIONS"
                        option-label="label"
                        option-value="value"
                        :allow-empty="false"
                        size="small"
                        aria-label="When the word already has a card"
                      />
                    </div>
                    <div class="flex flex-col gap-1">
                      <span class="text-xs text-gray-500 dark:text-gray-400">When it has none</span>
                      <SelectButton
                        v-model="s.noCard"
                        :options="NO_CARD_OPTIONS"
                        option-label="label"
                        option-value="value"
                        :allow-empty="false"
                        size="small"
                        aria-label="When the word has no card"
                      />
                    </div>
                    <div v-if="needsTargets(s)" class="flex flex-col gap-1">
                      <span class="text-xs text-gray-500 dark:text-gray-400">New cards go to the first target whose rule matches the line</span>
                      <PipelineTargetsEditor
                        v-model="s.targets"
                        :id-prefix="`p${pi}s${si}`"
                        :decks="decks"
                        :models="models"
                        :loading="ankiLoading"
                        :warnings="targetWarnings(s)"
                      />
                    </div>
                  </template>

                  <template v-else>
                    <div class="flex flex-col gap-1 sm:max-w-md">
                      <label :for="`p${pi}s${si}-tags`" class="text-xs text-gray-500 dark:text-gray-400">Tags, separated by spaces</label>
                      <InputText :id="`p${pi}s${si}-tags`" v-model="s.tags" size="small" />
                    </div>
                    <div class="flex flex-wrap gap-4">
                      <div class="flex items-center gap-2">
                        <Checkbox v-model="s.titleTag" :input-id="`p${pi}s${si}-title-tag`" binary />
                        <label :for="`p${pi}s${si}-title-tag`" class="text-sm">Also the title</label>
                      </div>
                      <div class="flex items-center gap-2">
                        <Checkbox v-model="s.mediaTypeTag" :input-id="`p${pi}s${si}-media-tag`" binary />
                        <label :for="`p${pi}s${si}-media-tag`" class="text-sm">Also the media type</label>
                      </div>
                    </div>
                    <small class="text-xs text-gray-500 dark:text-gray-400">Tags go on new cards only.</small>
                  </template>

                  <PipelineConditionEditor v-model="s.when" :id-prefix="`p${pi}s${si}-when`" :note-types="noteTypes" />
                </div>
              </li>
            </ol>
            <p v-else class="rounded border border-dashed border-surface-300 dark:border-surface-600 p-2 text-sm text-gray-500 dark:text-gray-400">
              No steps: the sentence is saved to the word's list for Jiten study and stays off Anki.
            </p>

            <div class="flex flex-wrap gap-2">
              <Button v-for="kind in ['build', 'anki', 'tags'] as const" :key="kind" :label="KIND_LABELS[kind]" icon="pi pi-plus" size="small" severity="secondary" outlined @click="addStep(p, kind, pi)" />
            </div>
          </div>
        </template>
      </Card>

      <div>
        <Button label="New pipeline" icon="pi pi-plus" severity="secondary" outlined :disabled="draft.pipelines.length >= 20" @click="addPipeline" />
      </div>

      <Card>
        <template #title>Default on each page</template>
        <template #content>
          <p class="mb-3 text-sm text-gray-600 dark:text-gray-300">The sentence button runs the page's default. ▾ lists every pipeline.</p>
          <div class="grid grid-cols-1 gap-3 sm:grid-cols-2 lg:grid-cols-4">
            <div v-for="page in PIPELINE_PAGES" :key="page.key" class="flex flex-col gap-1 min-w-0">
              <label :for="`default-${page.key}`" class="text-xs text-gray-500 dark:text-gray-400">{{ page.label }}</label>
              <Select v-model="draft.defaults[page.key]" :input-id="`default-${page.key}`" :options="draft.pipelines" option-label="name" option-value="id" size="small" />
            </div>
          </div>
        </template>
      </Card>
    </template>
    <p v-else-if="!loadError" class="text-sm text-gray-500">Loading your pipelines…</p>

    <div
      v-if="draft && (dirty || !draft.saved)"
      class="sticky bottom-0 z-10 -mx-2 md:-mx-4 border-t border-surface-200 dark:border-surface-700 bg-white/95 dark:bg-surface-900/95 px-4 py-3"
    >
      <div class="flex flex-wrap items-center justify-end gap-3">
        <small v-if="saveError || problem" class="text-red-500 mr-auto">{{ saveError || problem }}</small>
        <Button label="Discard changes" severity="secondary" text :disabled="!dirty || saving" @click="reset(store.pipelines.value!)" />
        <Button label="Save" icon="pi pi-check" :disabled="!!problem" :loading="saving" @click="save" />
      </div>
    </div>
  </div>
</template>
