<script setup lang="ts">
  import { MediaType } from '~/types/enums';
  import type { MediaSuggestion } from '~/types/types';
  import { getMediaTypeText } from '~/utils/mediaTypeMapper';
  import type { PipelineTarget } from '~/utils/pipelines';

  /** An Anki step's targets for new notes: the first whose rule matches the line; the last takes everything else. */
  const targets = defineModel<PipelineTarget[]>({ required: true });
  defineProps<{
    idPrefix: string;
    decks: string[];
    models: string[];
    loading: boolean;
    /** Per target index: why its cards won't sync or can't be made. */
    warnings: Record<number, string>;
  }>();

  const localiseTitle = useLocaliseTitle();
  const MEDIA_TYPE_OPTIONS = (Object.values(MediaType).filter((v) => typeof v === 'number') as MediaType[]).map((value) => ({
    value,
    label: getMediaTypeText(value),
  }));
  const titlePick = ref<Record<number, number | null>>({});

  function ruleOf(t: PipelineTarget) {
    t.when ??= { mediaTypes: [], titles: [] };
    return t.when;
  }

  function ruleEmpty(t: PipelineTarget) {
    return !t.when || (t.when.mediaTypes.length === 0 && t.when.titles.length === 0);
  }

  function addTitle(i: number, suggestion: MediaSuggestion | null) {
    const t = targets.value[i];
    if (!suggestion || !t) return;
    const rule = ruleOf(t);
    if (!rule.titles.some((x) => x.deckId === suggestion.deckId)) rule.titles.push({ deckId: suggestion.deckId, title: localiseTitle(suggestion) });
    nextTick(() => (titlePick.value[i] = null));
  }

  function add() {
    // A new target goes before the last, which takes everything else.
    const last = targets.value[targets.value.length - 1];
    targets.value.splice(Math.max(0, targets.value.length - 1), 0, {
      deckName: last?.deckName ?? '',
      modelName: last?.modelName ?? '',
      when: { mediaTypes: [], titles: [] },
    });
  }

  function move(i: number, by: -1 | 1) {
    const j = i + by;
    if (j < 0 || j >= targets.value.length) return;
    const list = [...targets.value];
    [list[i], list[j]] = [list[j]!, list[i]!];
    targets.value = list;
  }
</script>

<template>
  <div class="flex flex-col gap-2">
    <div v-for="(t, i) in targets" :key="i" class="flex flex-col gap-2 rounded border border-surface-200 dark:border-surface-700 p-2" data-testid="pipeline-target">
      <div class="flex items-center justify-between gap-2 flex-wrap">
        <span class="text-xs font-semibold">
          {{ i + 1 }}. {{ i < targets.length - 1 ? 'When the line is from' : targets.length > 1 ? 'Everything else' : 'Every line' }}
        </span>
        <div class="flex gap-1">
          <Button icon="pi pi-arrow-up" size="small" severity="secondary" text :disabled="i === 0" aria-label="Move target up" @click="move(i, -1)" />
          <Button icon="pi pi-arrow-down" size="small" severity="secondary" text :disabled="i === targets.length - 1" aria-label="Move target down" @click="move(i, 1)" />
          <Button icon="pi pi-trash" size="small" severity="secondary" text :disabled="targets.length === 1" aria-label="Remove target" @click="targets.splice(i, 1)" />
        </div>
      </div>

      <div v-if="i < targets.length - 1" class="flex flex-col gap-2">
        <div class="grid grid-cols-1 sm:grid-cols-2 gap-2">
          <div class="flex flex-col gap-1 min-w-0">
            <label :for="`${idPrefix}-t${i}-media`" class="text-xs text-gray-500 dark:text-gray-400">Media types</label>
            <MultiSelect
              v-model="ruleOf(t).mediaTypes"
              :input-id="`${idPrefix}-t${i}-media`"
              :options="MEDIA_TYPE_OPTIONS"
              option-label="label"
              option-value="value"
              display="chip"
              placeholder="None"
              size="small"
              class="w-full"
            />
          </div>
          <div class="flex flex-col gap-1 min-w-0">
            <label :for="`${idPrefix}-t${i}-title`" class="text-xs text-gray-500 dark:text-gray-400">or titles</label>
            <MediaDeckPicker
              v-model="titlePick[i]"
              :input-id="`${idPrefix}-t${i}-title`"
              :allow-raw-id="false"
              placeholder="Search for a title..."
              @select="(s: MediaSuggestion | null) => addTitle(i, s)"
            />
          </div>
        </div>
        <div v-if="t.when?.titles.length" class="flex flex-wrap gap-2">
          <Chip v-for="(title, j) in t.when.titles" :key="title.deckId" :label="title.title" removable @remove="t.when!.titles.splice(j, 1)" />
        </div>
        <small v-if="ruleEmpty(t)" class="text-red-500">Choose a media type or a title; only the last target takes everything else.</small>
      </div>

      <div class="grid grid-cols-1 sm:grid-cols-2 gap-2">
        <div class="flex flex-col gap-1 min-w-0">
          <label :for="`${idPrefix}-t${i}-deck`" class="text-xs text-gray-500 dark:text-gray-400">Deck</label>
          <Select v-model="t.deckName" :input-id="`${idPrefix}-t${i}-deck`" :options="decks" filter editable :loading="loading" placeholder="Choose a deck" size="small" />
        </div>
        <div class="flex flex-col gap-1 min-w-0">
          <label :for="`${idPrefix}-t${i}-model`" class="text-xs text-gray-500 dark:text-gray-400">Note type</label>
          <Select v-model="t.modelName" :input-id="`${idPrefix}-t${i}-model`" :options="models" filter editable :loading="loading" placeholder="Choose a note type" size="small" />
        </div>
      </div>
      <Message v-if="warnings[i]" severity="warn" size="small" :closable="false">
        {{ warnings[i] }} <NuxtLink to="/settings/anki" class="underline">Anki settings</NuxtLink>
      </Message>
    </div>
    <div>
      <Button label="Add a target" icon="pi pi-plus" size="small" severity="secondary" outlined @click="add" />
    </div>
  </div>
</template>
