<script setup lang="ts">
  import type { MediaSuggestion } from '~/types/types';
  import type { AutoSentences } from '~/utils/ankiSetups';

  /** A note type's automatic sentences settings, edited in place, with what the next Sync would add. */
  const props = defineProps<{ modelName: string; saved: boolean }>();
  const model = defineModel<AutoSentences>({ required: true });

  const { $api } = useNuxtApp();
  const localiseTitle = useLocaliseTitle();

  const ENGLISH_OPTIONS = [
    { value: 'subtitlesOnly', label: 'Subtitles only' },
    { value: 'subtitlesThenAi', label: 'Subtitles, then AI' },
    { value: 'none', label: 'None' },
  ];
  const OTHERS_OPTIONS = [
    { value: false, label: 'Known (Young or better)' },
    { value: true, label: 'Mature' },
  ];

  function set<K extends keyof AutoSentences>(key: K, value: AutoSentences[K]) {
    model.value = { ...model.value, [key]: value };
  }

  const titlePick = ref<number | null>(null);
  function addTitle(suggestion: MediaSuggestion | null) {
    if (!suggestion) return;
    const titles = model.value.excludedTitles ?? [];
    if (!titles.some((t) => t.deckId === suggestion.deckId))
      set('excludedTitles', [...titles, { deckId: suggestion.deckId, title: localiseTitle(suggestion) }]);
    nextTick(() => (titlePick.value = null));
  }

  function removeTitle(deckId: number) {
    set(
      'excludedTitles',
      (model.value.excludedTitles ?? []).filter((t) => t.deckId !== deckId)
    );
  }

  // What the next Sync would add with the saved settings, counted on request (it runs the whole plan).
  const preview = ref<{ words: number; lines: number; maxPerSync: number } | null>(null);
  const previewing = ref(false);
  const previewError = ref<string | null>(null);

  async function loadPreview() {
    previewing.value = true;
    previewError.value = null;
    try {
      preview.value = await $api('mined/auto/preview', { query: { modelName: props.modelName } });
    } catch (e) {
      preview.value = null;
      previewError.value = extractApiError(e, "Couldn't count");
    } finally {
      previewing.value = false;
    }
  }

  // A count is stale once the settings change.
  watch(
    () => props.saved,
    (saved) => {
      if (!saved) preview.value = null;
    }
  );
</script>

<template>
  <div class="flex flex-col gap-2" :data-testid="`auto-${modelName}`">
    <label class="flex items-center gap-2 text-sm font-semibold">
      <ToggleSwitch :model-value="model.enabled" :aria-label="`Automatic sentences for ${modelName}`" @update:model-value="(v: boolean) => set('enabled', v)" />
      Add sentences automatically on Sync
    </label>
    <small class="text-xs text-gray-500 dark:text-gray-400">
      Each Sync adds lines from imported anime, TV and film to this note type's cards, where the card's word is the only word you don't know yet. Your own
      sentences stay first; a line you remove from Mined words isn't added again.
    </small>

    <div v-if="model.enabled" class="grid grid-cols-1 gap-3 rounded border border-surface-200 p-3 dark:border-surface-700 md:grid-cols-2">
      <div class="flex flex-col gap-1">
        <label :for="`auto-per-card-${modelName}`" class="text-sm">Sentences per card</label>
        <InputNumber
          :model-value="model.perCard"
          :input-id="`auto-per-card-${modelName}`"
          :min="1"
          :max="20"
          show-buttons
          size="small"
          @update:model-value="(v: number | null) => set('perCard', v ?? 5)"
        />
      </div>
      <div class="flex flex-col gap-1">
        <label :for="`auto-per-sync-${modelName}`" class="text-sm">Added per card in one Sync</label>
        <InputNumber
          :model-value="model.perSync"
          :input-id="`auto-per-sync-${modelName}`"
          :min="1"
          :max="20"
          show-buttons
          size="small"
          @update:model-value="(v: number | null) => set('perSync', v ?? 2)"
        />
      </div>
      <div class="flex flex-col gap-1">
        <label :for="`auto-others-${modelName}`" class="text-sm">Other words must be</label>
        <Select
          :model-value="model.othersMature"
          :input-id="`auto-others-${modelName}`"
          :options="OTHERS_OPTIONS"
          option-label="label"
          option-value="value"
          size="small"
          @update:model-value="(v: boolean) => set('othersMature', v)"
        />
      </div>
      <div class="flex flex-col gap-1">
        <span class="text-sm">Length (characters)</span>
        <div class="flex items-center gap-2">
          <InputNumber
            :model-value="model.lengthMin"
            :min="0"
            :max="500"
            size="small"
            input-class="w-16"
            :aria-label="`Shortest line for ${modelName}`"
            @update:model-value="(v: number | null) => set('lengthMin', v ?? 0)"
          />
          <span>to</span>
          <InputNumber
            :model-value="model.lengthMax"
            :min="1"
            :max="500"
            size="small"
            input-class="w-16"
            :aria-label="`Longest line for ${modelName}`"
            @update:model-value="(v: number | null) => set('lengthMax', v ?? 40)"
          />
        </div>
      </div>
      <div class="flex flex-col gap-1">
        <span class="text-sm">Only lines with</span>
        <label class="flex items-center gap-2 text-sm"
          ><Checkbox :model-value="model.needsClip" binary @update:model-value="(v: boolean) => set('needsClip', v)" /> a clip (the title has video)</label
        >
        <label class="flex items-center gap-2 text-sm"
          ><Checkbox :model-value="model.needsEnglish" binary @update:model-value="(v: boolean) => set('needsEnglish', v)" /> English</label
        >
      </div>
      <div class="flex flex-col gap-1">
        <span class="text-sm">Each line gets</span>
        <div class="flex flex-wrap gap-3">
          <label class="flex items-center gap-2 text-sm"
            ><Checkbox :model-value="model.audio" binary @update:model-value="(v: boolean) => set('audio', v)" /> Audio</label
          >
          <label class="flex items-center gap-2 text-sm"
            ><Checkbox :model-value="model.picture" binary @update:model-value="(v: boolean) => set('picture', v)" /> Screenshot</label
          >
          <label class="flex items-center gap-2 text-sm"
            ><Checkbox :model-value="model.video" binary @update:model-value="(v: boolean) => set('video', v)" /> Clip</label
          >
        </div>
        <Select
          :model-value="model.english"
          :options="ENGLISH_OPTIONS"
          option-label="label"
          option-value="value"
          size="small"
          :aria-label="`English for ${modelName}'s automatic sentences`"
          @update:model-value="(v: AutoSentences['english']) => set('english', v)"
        />
      </div>
      <div class="flex flex-col gap-1 md:col-span-2">
        <span class="text-sm">Leave out titles</span>
        <div v-if="model.excludedTitles?.length" class="flex flex-wrap gap-1">
          <Chip v-for="t in model.excludedTitles" :key="t.deckId" :label="t.title" removable @remove="removeTitle(t.deckId)" />
        </div>
        <MediaDeckPicker
          v-model="titlePick"
          :input-id="`auto-title-${modelName}`"
          :allow-raw-id="false"
          placeholder="Search for a title..."
          @select="addTitle"
        />
      </div>
      <div class="text-sm md:col-span-2" data-testid="auto-preview">
        <template v-if="!saved">Save to see what the next Sync would add.</template>
        <i v-else-if="previewing" class="pi pi-spin pi-spinner text-gray-500" />
        <template v-else-if="previewError">{{ previewError }}</template>
        <template v-else-if="preview">
          Next Sync would add about <b>{{ preview.lines }}</b> sentence{{ preview.lines === 1 ? '' : 's' }} to <b>{{ preview.words }}</b> word{{
            preview.words === 1 ? '' : 's'
          }}<template v-if="preview.lines > preview.maxPerSync"> ({{ preview.maxPerSync }} at most per Sync, across note types)</template>.
          <button class="ml-1 underline text-gray-500" @click="loadPreview">Count again</button>
        </template>
        <Button v-else label="What would the next Sync add?" icon="pi pi-calculator" size="small" severity="secondary" outlined @click="loadPreview" />
      </div>
    </div>
  </div>
</template>
