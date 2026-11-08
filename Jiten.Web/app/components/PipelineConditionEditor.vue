<script setup lang="ts">
  import { MediaType } from '~/types/enums';
  import type { MediaSuggestion } from '~/types/types';
  import { getMediaTypeText } from '~/utils/mediaTypeMapper';
  import type { PipelineCondition } from '~/utils/pipelines';

  /** A pipeline step's "only if": the line's media type or title, and the note type of the word's card. Null: always. */
  const condition = defineModel<PipelineCondition | null>({ required: true });
  const props = defineProps<{
    /** For the inputs' ids. */
    idPrefix: string;
    /** Note types to offer for the word's card. */
    noteTypes: string[];
  }>();

  const localiseTitle = useLocaliseTitle();
  const MEDIA_TYPE_OPTIONS = (Object.values(MediaType).filter((v) => typeof v === 'number') as MediaType[]).map((value) => ({
    value,
    label: getMediaTypeText(value),
  }));
  const titlePick = ref<number | null>(null);

  function addTitle(suggestion: MediaSuggestion | null) {
    const c = condition.value;
    if (!suggestion || !c) return;
    if (!c.titles.some((t) => t.deckId === suggestion.deckId)) c.titles.push({ deckId: suggestion.deckId, title: localiseTitle(suggestion) });
    nextTick(() => (titlePick.value = null));
  }

  const noteTypeOptions = computed(() => [...new Set([...props.noteTypes, ...(condition.value?.cardNoteTypes ?? [])])]);
</script>

<template>
  <div v-if="!condition">
    <Button label="Only if…" icon="pi pi-filter" size="small" severity="secondary" text @click="condition = { mediaTypes: [], titles: [], cardNoteTypes: [] }" />
    <span class="text-xs text-gray-500 dark:text-gray-400">Runs for every sentence.</span>
  </div>
  <div v-else class="flex flex-col gap-2 rounded border border-dashed border-surface-300 dark:border-surface-600 p-2">
    <div class="flex items-center justify-between gap-2">
      <span class="text-xs font-semibold text-gray-600 dark:text-gray-300">Only if</span>
      <Button label="Always run" size="small" severity="secondary" text @click="condition = null" />
    </div>
    <div class="grid grid-cols-1 sm:grid-cols-2 gap-2">
      <div class="flex flex-col gap-1 min-w-0">
        <label :for="`${idPrefix}-media`" class="text-xs text-gray-500 dark:text-gray-400">The line is from these media types</label>
        <MultiSelect
          v-model="condition.mediaTypes"
          :input-id="`${idPrefix}-media`"
          :options="MEDIA_TYPE_OPTIONS"
          option-label="label"
          option-value="value"
          display="chip"
          placeholder="Any"
          size="small"
          class="w-full"
        />
      </div>
      <div class="flex flex-col gap-1 min-w-0">
        <label :for="`${idPrefix}-title`" class="text-xs text-gray-500 dark:text-gray-400">or these titles</label>
        <MediaDeckPicker v-model="titlePick" :input-id="`${idPrefix}-title`" :allow-raw-id="false" placeholder="Search for a title..." @select="addTitle" />
      </div>
    </div>
    <div v-if="condition.titles.length" class="flex flex-wrap gap-2">
      <Chip v-for="(title, j) in condition.titles" :key="title.deckId" :label="title.title" removable @remove="condition.titles.splice(j, 1)" />
    </div>
    <div class="flex flex-col gap-1 min-w-0 sm:max-w-96">
      <label :for="`${idPrefix}-card`" class="text-xs text-gray-500 dark:text-gray-400">The word's Anki card is on</label>
      <MultiSelect
        v-model="condition.cardNoteTypes"
        :input-id="`${idPrefix}-card`"
        :options="noteTypeOptions"
        display="chip"
        placeholder="Any word, with or without a card"
        size="small"
        class="w-full"
      />
    </div>
  </div>
</template>
