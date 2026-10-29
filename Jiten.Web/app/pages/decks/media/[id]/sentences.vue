<script setup lang="ts">
  import type { DeckDetail, DeckIPlusOneSentence, DeckIPlusOneSentencesResponse, SentenceSegmentBar, StudyDeckDto, Word, WordSummary } from '~/types';
  import { DeckOrder, KnownState, MediaType, StudyDeckType } from '~/types';
  import { useApiFetchPaginated } from '~/composables/useApiFetch';
  import { useAuthStore } from '~/stores/authStore';
  import { useJitenStore } from '~/stores/jitenStore';
  import { useSrsStore } from '~/stores/srsStore';

  definePageMeta({
    validate: (route) => /^\d+$/.test(String(route.params.id)),
  });

  const route = useRoute();
  const auth = useAuthStore();
  const jitenStore = useJitenStore();
  const localiseTitle = useLocaliseTitle();
  const convertToRuby = useConvertToRuby();
  const deckId = computed(() => Number(route.params.id));

  const { data: deckResponse, error: deckError, ready: deckReady } = await useApiFetchPaginated<DeckDetail>(`media-deck/${deckId.value}/detail`);

  if (import.meta.server) {
    await deckReady;
    if (isMissingResource(deckError.value, deckResponse.value?.data)) throw createError({ statusCode: 404, statusMessage: 'Deck not found', fatal: true });
  }

  const mainDeck = computed(() => deckResponse.value?.data?.mainDeck);
  const title = computed(() => {
    const data = deckResponse.value?.data;
    if (!data) return '';
    return (data.parentDeck ? localiseTitle(data.parentDeck) + ' - ' : '') + localiseTitle(data.mainDeck);
  });

  useRobotsRule({ noindex: true, follow: true });
  useHead(() => ({ title: `${title.value} - Sentences` }));

  const { stats, loading, failed, rateLimited, granted, statusReady, knownVersion, retry } = useSentenceStats(deckId, false);

  const mounted = ref(false);
  onMounted(() => (mounted.value = true));
  const showSkeleton = computed(() => !mounted.value || !statusReady.value || (granted.value && loading.value && !stats.value));
  const hasData = computed(() => granted.value && !!stats.value?.hasData);

  const percentOf = (count: number) => (stats.value && stats.value.total > 0 ? (count * 100) / stats.value.total : 0);
  const formatPercent = (count: number) => `${Math.floor(percentOf(count))}%`;

  const partUnit = computed(() => {
    switch (mainDeck.value?.mediaType) {
      case MediaType.Anime:
      case MediaType.Drama:
      case MediaType.Audio:
        return { one: 'Episode', many: 'episodes' };
      case MediaType.Novel:
      case MediaType.NonFiction:
      case MediaType.Manga:
        return { one: 'Volume', many: 'volumes' };
      case MediaType.WebNovel:
        return { one: 'Chapter', many: 'chapters' };
      case MediaType.YouTube:
        return { one: 'Video', many: 'videos' };
      default:
        return { one: 'Part', many: 'parts' };
    }
  });

  const segmentBars = computed<SentenceSegmentBar[]>(() => {
    const s = stats.value;
    if (!s) return [];
    const count = s.segments.length;
    return s.segments.map((segment) => {
      if (s.segmentsArePart) {
        const label = segment.firstPart === segment.lastPart ? `${segment.firstPart}` : `${segment.firstPart}-${segment.lastPart}`;
        const tooltipTitle = segment.title ?? `${partUnit.value.one}${segment.firstPart === segment.lastPart ? '' : 's'} ${label}`;
        return { label, tooltipTitle, total: segment.total, readable: segment.readable, oneUnknown: segment.oneUnknown, twoUnknown: segment.twoUnknown };
      }
      const from = Math.round((segment.index * 100) / count);
      const to = Math.round(((segment.index + 1) * 100) / count);
      return {
        label: `${from}%`,
        tooltipTitle: `${from}% to ${to}% of the text`,
        total: segment.total,
        readable: segment.readable,
        oneUnknown: segment.oneUnknown,
        twoUnknown: segment.twoUnknown,
      };
    });
  });

  const segmentsTitle = computed(() =>
    stats.value?.segmentsArePart ? `Readable sentences per ${partUnit.value.one.toLowerCase()}` : 'Readable sentences through the text'
  );

  const learnNextShown = ref(20);
  const markedKnown = ref(new Set<string>());
  const wordKey = (word: WordSummary) => `${word.wordId}-${word.readingIndex}`;
  // VocabularyStatus only reads wordId, mainReading and knownStates; every learn-next word is unknown.
  const statusWord = (word: WordSummary) =>
    ({ wordId: word.wordId, mainReading: { text: word.reading, readingIndex: word.readingIndex }, knownStates: [] }) as unknown as Word;
  const learnNext = computed(() =>
    (stats.value?.learnNext ?? [])
      .filter((step) => !markedKnown.value.has(wordKey(step.word)))
      .slice(0, learnNextShown.value)
      .map((step) => ({ ...step, statusWord: statusWord(step.word) }))
  );

  function onStatusChanged(word: WordSummary, states: KnownState[]) {
    if (!states.some((state) => state === KnownState.Mastered || state === KnownState.Blacklisted)) return;
    markedKnown.value = new Set(markedKnown.value).add(wordKey(word));
    jitenStore.bumpDeckCoverageVersion(deckId.value);
    if (mainDeck.value?.parentDeckId) jitenStore.bumpDeckCoverageVersion(mainDeck.value.parentDeckId);
  }

  const srsStore = useSrsStore();
  const studyDialog = ref<{ editDeck?: StudyDeckDto } | null>(null);
  const openingStudyDialog = ref(false);

  // A title already in the study list can't be added twice, so its existing deck is edited instead.
  async function studyInUnlockOrder() {
    openingStudyDialog.value = true;
    try {
      await srsStore.fetchStudyDecks();
    } finally {
      openingStudyDialog.value = false;
    }
    const existing = srsStore.studyDecks.find((d) => d.deckType === StudyDeckType.MediaDeck && d.deckId === deckId.value);
    studyDialog.value = { editDeck: existing };
  }

  const milestones = computed(() => (stats.value?.milestones ?? []).filter((m) => percentOf(stats.value!.readable) < m.percent));

  const mineSentences = ref<DeckIPlusOneSentence[]>([]);
  const mineTotal = ref(0);
  const mineLoading = ref(false);
  const mineFailed = ref(false);

  const mineGroups = computed(() => {
    const groups: { key: string; word: DeckIPlusOneSentence['word']; wordSentences: number; sentences: DeckIPlusOneSentence['sentence'][] }[] = [];
    for (const item of mineSentences.value) {
      const key = `${item.word.wordId}-${item.word.readingIndex}`;
      const last = groups.at(-1);
      if (last?.key === key) last.sentences.push(item.sentence);
      else groups.push({ key, word: item.word, wordSentences: item.wordSentences, sentences: [item.sentence] });
    }
    return groups;
  });

  async function loadMineSentences(reset = false) {
    if (!hasData.value || mineLoading.value) return;
    mineLoading.value = true;
    mineFailed.value = false;
    try {
      const { $api } = useNuxtApp();
      const offset = reset ? 0 : mineSentences.value.length;
      const page = await $api<DeckIPlusOneSentencesResponse>(`media-deck/${deckId.value}/iplusone-sentences`, { query: { offset, v: knownVersion.value } });
      mineSentences.value = reset ? page.sentences : [...mineSentences.value, ...page.sentences];
      mineTotal.value = page.total;
    } catch {
      mineFailed.value = true;
    } finally {
      mineLoading.value = false;
    }
  }

  // Mining: preview the sentence audio cut from the linked episode, and add the sentence to Anki through AnkiConnect.
  const { configured: cardsConfigured, fetchAudio, addCard } = useMiningCards();
  const toast = useToast();
  const cardSettingsVisible = ref(false);
  const cardState = ref<Record<number, 'adding' | 'added'>>({});
  const audioLoading = ref<number | null>(null);
  const audioUrls = new Map<number, string>();
  let player: HTMLAudioElement | null = null;

  function mediaError(e: unknown, fallback: string): string {
    const err = e as { statusCode?: number; response?: { status?: number }; message?: string } | null;
    if (err?.statusCode === 404 || err?.response?.status === 404) return "This sentence's episode has no linked video.";
    if (e instanceof TypeError) return "Couldn't reach AnkiConnect at 127.0.0.1:8765. Is Anki open?";
    return err?.message ? `${fallback}: ${err.message}` : fallback;
  }

  // Without a linked episode the sentence is read by text-to-speech instead.
  const { speakSentence, stop: stopTts } = useTts(ref(''), 'sentence');
  const ttsOnly = new Set<number>();

  async function playSentence(sentenceId: number, text: string) {
    player?.pause();
    stopTts();
    if (ttsOnly.has(sentenceId)) return speakSentence(sentenceId, text);
    let url = audioUrls.get(sentenceId);
    if (!url) {
      audioLoading.value = sentenceId;
      try {
        url = URL.createObjectURL(await fetchAudio(sentenceId));
        audioUrls.set(sentenceId, url);
      } catch (e) {
        const err = e as { statusCode?: number; response?: { status?: number } } | null;
        if (err?.statusCode === 404 || err?.response?.status === 404) {
          ttsOnly.add(sentenceId);
          speakSentence(sentenceId, text);
          return;
        }
        toast.add({ severity: 'warn', summary: mediaError(e, "Couldn't load the audio"), life: 4000, group: 'bottom' });
        return;
      } finally {
        audioLoading.value = null;
      }
    }
    player = new Audio(url);
    player.play();
  }

  async function mineSentence(word: DeckIPlusOneSentence['word'], sentence: DeckIPlusOneSentence['sentence']) {
    if (!cardsConfigured.value) {
      cardSettingsVisible.value = true;
      return;
    }
    cardState.value = { ...cardState.value, [sentence.sentenceId]: 'adding' };
    try {
      await addCard({ word, sentence, wordSentences: 0 });
      cardState.value = { ...cardState.value, [sentence.sentenceId]: 'added' };
      toast.add({ severity: 'success', summary: `Added ${word.reading} to Anki`, life: 2000, group: 'bottom' });
    } catch (e) {
      const { [sentence.sentenceId]: _, ...rest } = cardState.value;
      cardState.value = rest;
      toast.add({ severity: 'error', summary: mediaError(e, "Couldn't add the card"), life: 5000, group: 'bottom' });
    }
  }

  // English: shown per the admin's page mode, from the episode's English subtitles or (for admins) an AI translation.
  const { options: translationOptions, translations, loadOptions: loadTranslationOptions, translate, translateMany } = useSentenceTranslation();
  const translating = ref<Record<number, boolean>>({});
  const pageMode = computed(() => translationOptions.value?.pageMode ?? 'off');

  async function showEnglish(sentenceId: number) {
    translating.value = { ...translating.value, [sentenceId]: true };
    try {
      const translation = await translate(sentenceId, !!translationOptions.value?.aiAvailable);
      if (!translation) toast.add({ severity: 'info', summary: 'No English for this sentence yet.', life: 3000, group: 'bottom' });
    } catch (e) {
      const message = (e as { data?: { message?: string } })?.data?.message;
      toast.add({ severity: 'error', summary: message ?? "Couldn't translate the sentence", life: 5000, group: 'bottom' });
    } finally {
      translating.value = { ...translating.value, [sentenceId]: false };
    }
  }

  // Auto mode: subtitle and cached English for every listed sentence; new AI translations only when the admin allows it.
  async function autoTranslate(sentenceIds: number[]) {
    const o = await loadTranslationOptions();
    if (o?.pageMode !== 'auto' || sentenceIds.length === 0) return;
    await translateMany(sentenceIds).catch(() => {});
    if (!o.pageAutoAi || !o.aiAvailable) return;
    for (const id of sentenceIds) if (translations.value[id] === null) await showEnglish(id);
  }

  function englishTitle(sentenceId: number): string {
    const t = translations.value[sentenceId];
    if (!t) return '';
    return t.origin === 'subtitles' ? `From ${t.source ?? 'English subtitles'}` : `AI translation (${t.model ?? t.provider})`;
  }

  watch(mineSentences, (items) => autoTranslate(items.map((i) => i.sentence.sentenceId)));

  onBeforeUnmount(() => {
    player?.pause();
    for (const url of audioUrls.values()) URL.revokeObjectURL(url);
  });

  if (import.meta.client) {
    watch(hasData, (ready) => ready && loadMineSentences(true), { immediate: true });
    loadTranslationOptions();
    watch(knownVersion, () => hasData.value && loadMineSentences(true));
  }
</script>

<template>
  <div class="flex flex-col gap-4">
    <DeckBreadcrumb :deck="mainDeck" :parent-deck="deckResponse?.data?.parentDeck" current="Sentences" />

    <h1 v-if="title" class="text-2xl font-bold">{{ title }} - Sentences</h1>

    <Card v-if="showSkeleton" class="p-2">
      <template #content>
        <Skeleton width="100%" height="160px" />
      </template>
    </Card>

    <Card v-else-if="!auth.isAuthenticated || !granted">
      <template #content>
        <JitenPlusGate feature="sentence-stats" feature-label="Sentence stats">
          <div class="flex flex-col gap-3">
            <p class="text-gray-600 dark:text-gray-300">
              How many of this title's sentences you can understand today, which words unlock the most new ones, and the sentences you can mine. (example)
            </p>
            <SentenceDistributionBar :readable="38" :one-unknown="21" :two-unknown="17" :three-or-more-unknown="24" />
          </div>
        </JitenPlusGate>
      </template>
    </Card>

    <Card v-else-if="rateLimited || failed">
      <template #content>
        <div class="flex flex-wrap items-center justify-between gap-2">
          <span class="text-gray-500 dark:text-gray-400">Couldn't load your sentence stats just now.</span>
          <Button label="Try again" icon="pi pi-refresh" severity="secondary" outlined size="small" @click="retry()" />
        </div>
      </template>
    </Card>

    <Card v-else-if="!hasData">
      <template #content>
        <p class="text-gray-500 dark:text-gray-400">Sentence stats for this title aren't ready yet.</p>
      </template>
    </Card>

    <template v-else-if="stats">
      <Card>
        <template #content>
          <div class="flex flex-col gap-4">
            <SentenceReadableHeadline :readable="stats.readable" :one-unknown="stats.oneUnknown" :total="stats.total" />
            <SentenceDistributionBar
              :readable="stats.readable"
              :one-unknown="stats.oneUnknown"
              :two-unknown="stats.twoUnknown"
              :three-or-more-unknown="stats.threeOrMoreUnknown"
            />
            <p v-if="stats.profiledParts < stats.totalParts" class="text-xs text-gray-500 dark:text-gray-400">
              Based on {{ stats.profiledParts }} of {{ stats.totalParts }} {{ partUnit.many }}; the rest are still being processed.
            </p>
          </div>
        </template>
      </Card>

      <Card v-if="segmentBars.length > 1">
        <template #header>
          <h2 class="text-xl font-bold px-4 pt-4">{{ segmentsTitle }}</h2>
        </template>
        <template #content>
          <LazySentenceSegmentsChart :segments="segmentBars" :x-title="stats.segmentsArePart ? partUnit.one : 'Position in the text'" />
        </template>
      </Card>

      <Card v-if="stats.learnNext.length > 0">
        <template #header>
          <div class="px-4 pt-4 flex flex-col sm:flex-row sm:items-start sm:justify-between gap-3">
            <div>
              <h2 class="text-xl font-bold">Words that make the most sentences understandable</h2>
              <p class="text-sm text-gray-600 dark:text-gray-300">
                If you learn the words in this order, they will make the most sentences understandable (i+0).
              </p>
            </div>
            <Button
              label="Study in this order"
              icon="pi pi-book"
              size="small"
              class="self-start shrink-0"
              :loading="openingStudyDialog"
              @click="studyInUnlockOrder"
            />
          </div>
        </template>
        <template #content>
          <div
            class="grid grid-cols-[1.25rem_minmax(0,1fr)_3.25rem_3.25rem_4.5rem] sm:grid-cols-[2rem_minmax(0,1fr)_5rem_5rem_5rem] gap-x-3 pb-1 text-xs text-gray-500 dark:text-gray-400 border-b border-surface-200 dark:border-surface-700"
          >
            <span />
            <span>Word</span>
            <span class="text-right">New sentences</span>
            <span class="text-right">Readable after</span>
            <span />
          </div>
          <ol class="flex flex-col">
            <li
              v-for="(step, index) in learnNext"
              :key="wordKey(step.word)"
              class="grid grid-cols-[1.25rem_minmax(0,1fr)_3.25rem_3.25rem_4.5rem] sm:grid-cols-[2rem_minmax(0,1fr)_5rem_5rem_5rem] items-baseline gap-x-3 py-2 border-b border-surface-200 dark:border-surface-700 last:border-b-0"
            >
              <span class="text-sm text-gray-500 dark:text-gray-400 tabular-nums">{{ index + 1 }}</span>
              <NuxtLink
                :to="`/vocabulary/${step.word.wordId}/${step.word.readingIndex}`"
                class="min-w-0 flex flex-col sm:flex-row sm:items-baseline sm:gap-3 hover:underline"
              >
                <span class="text-lg font-medium" lang="ja" v-html="convertToRuby(step.word.readingFurigana || step.word.reading)" />
                <span v-if="step.word.mainDefinition" class="text-sm text-gray-600 dark:text-gray-300 line-clamp-2 sm:line-clamp-none sm:truncate">{{
                  step.word.mainDefinition
                }}</span>
              </NuxtLink>
              <span class="text-right text-sm tabular-nums font-semibold text-green-700 dark:text-green-400">+{{ step.unlocked.toLocaleString() }}</span>
              <span class="text-right text-sm tabular-nums text-gray-600 dark:text-gray-300">{{ formatPercent(step.readableAfter) }}</span>
              <div class="justify-self-end self-center">
                <VocabularyStatus :word="step.statusWord" @changed="(states) => onStatusChanged(step.word, states)" />
              </div>
            </li>
          </ol>
          <div v-if="stats.learnNext.length > learnNextShown" class="flex justify-center pt-3">
            <Button :label="`Show all ${stats.learnNext.length}`" severity="secondary" outlined size="small" @click="learnNextShown = stats.learnNext.length" />
          </div>
        </template>
      </Card>

      <Card v-if="stats.projection.length > 1">
        <template #header>
          <h2 class="text-xl font-bold px-4 pt-4">Readable sentences as you learn new words</h2>
          <p class="text-sm text-gray-600 dark:text-gray-300 px-4 pt-1">
            A comparison of how many sentences become readable if you learn the words that unlock new sentences first vs the words that are the most frequent in
            the deck first.
          </p>
        </template>
        <template #content>
          <LazySentenceProjectionChart :points="stats.projection" :total="stats.total" />
          <div v-if="milestones.length > 0" class="grid grid-cols-2 sm:grid-cols-3 lg:grid-cols-5 gap-4 pt-6">
            <div v-for="milestone in milestones" :key="milestone.percent">
              <div class="text-sm text-gray-500 dark:text-gray-400">{{ milestone.percent }}% readable</div>
              <div class="text-xl font-bold tabular-nums">
                {{ milestone.words == null ? 'Over 2,000 words' : `${milestone.words.toLocaleString()} ${milestone.words === 1 ? 'word' : 'words'}` }}
              </div>
            </div>
          </div>
        </template>
      </Card>

      <Card>
        <template #header>
          <div class="px-4 pt-4 flex flex-col sm:flex-row sm:items-start sm:justify-between gap-3">
            <div>
              <h2 class="text-xl font-bold">Sentences you can mine</h2>
              <p class="text-sm text-gray-600 dark:text-gray-300">
                Some i+1 example sentences from this title, with the target word being the unknown.
                <NuxtLink :to="`/mine?titles=${deckResponse?.data?.parentDeck?.deckId ?? deckId}`" class="whitespace-nowrap">Sort and filter them on Mine</NuxtLink>
              </p>
            </div>
            <Button
              label="Card settings"
              icon="pi pi-cog"
              size="small"
              severity="secondary"
              outlined
              class="self-start shrink-0"
              @click="cardSettingsVisible = true"
            />
          </div>
        </template>
        <template #content>
          <div v-if="mineSentences.length === 0 && mineLoading" class="h-24 rounded bg-surface-100 dark:bg-surface-800 animate-pulse" />
          <p v-else-if="mineFailed && mineSentences.length === 0" class="text-gray-500 dark:text-gray-400">Couldn't load the sentences just now.</p>
          <p v-else-if="mineSentences.length === 0" class="text-gray-500 dark:text-gray-400">None of this title's example sentences are i+1 for you.</p>
          <template v-else>
            <p class="text-sm text-gray-500 dark:text-gray-400 mb-2">{{ mineTotal.toLocaleString() }} sentences</p>
            <ul class="flex flex-col divide-y divide-surface-200 dark:divide-surface-700">
              <li v-for="(group, index) in mineGroups" :key="`${group.key}-${index}`" class="py-4 first:pt-0 flex flex-col gap-2">
                <div class="flex flex-wrap items-baseline justify-between gap-x-3 gap-y-1">
                  <NuxtLink
                    :to="`/vocabulary/${group.word.wordId}/${group.word.readingIndex}`"
                    class="flex flex-wrap items-baseline gap-x-2 hover:underline"
                    target="_blank"
                  >
                    <span class="text-lg font-semibold text-primary-600 dark:text-primary-400" lang="ja">{{ group.word.reading }}</span>
                    <span v-if="group.word.mainDefinition" class="text-sm text-gray-600 dark:text-gray-300">{{ group.word.mainDefinition }}</span>
                  </NuxtLink>
                  <span v-if="group.wordSentences" class="text-xs text-gray-500 dark:text-gray-400 tabular-nums whitespace-nowrap">
                    {{ group.wordSentences.toLocaleString() }} {{ group.wordSentences === 1 ? 'sentence' : 'sentences' }}
                  </span>
                </div>
                <div v-for="sentence in group.sentences" :key="sentence.sentenceId" class="flex items-start gap-2">
                  <div class="min-w-0 flex-1">
                    <ExampleSentenceEntry :example-sentence="sentence" :word-id="group.word.wordId" :reading-index="group.word.readingIndex" show-source :show-tts="false" :show-mine="false" />
                    <p
                      v-if="pageMode !== 'off' && translations[sentence.sentenceId]"
                      class="text-sm text-gray-600 dark:text-gray-300 mt-1"
                      :title="englishTitle(sentence.sentenceId)"
                    >
                      {{ translations[sentence.sentenceId]!.text }}
                      <i v-if="translations[sentence.sentenceId]!.origin === 'ai'" class="pi pi-sparkles text-xs text-gray-400" aria-label="AI translation" />
                    </p>
                  </div>
                  <div class="flex shrink-0 gap-1">
                    <Button
                      v-if="pageMode === 'button' && !translations[sentence.sentenceId]"
                      v-tooltip.left="'Show the English'"
                      icon="pi pi-language"
                      size="small"
                      severity="secondary"
                      text
                      rounded
                      aria-label="Show the English"
                      :loading="translating[sentence.sentenceId]"
                      @click="showEnglish(sentence.sentenceId)"
                    />
                    <Button
                      v-tooltip.left="'Listen'"
                      icon="pi pi-play"
                      size="small"
                      severity="secondary"
                      text
                      rounded
                      aria-label="Play sentence audio"
                      :loading="audioLoading === sentence.sentenceId"
                      @click="playSentence(sentence.sentenceId, sentence.text)"
                    />
                    <Button
                      v-tooltip.left="cardState[sentence.sentenceId] === 'added' ? 'Added to Anki' : 'Add to Anki'"
                      :icon="cardState[sentence.sentenceId] === 'added' ? 'pi pi-check' : 'pi pi-plus'"
                      size="small"
                      :severity="cardState[sentence.sentenceId] === 'added' ? 'success' : 'secondary'"
                      text
                      rounded
                      :aria-label="cardState[sentence.sentenceId] === 'added' ? 'Added to Anki' : 'Add to Anki'"
                      :loading="cardState[sentence.sentenceId] === 'adding'"
                      :disabled="!!cardState[sentence.sentenceId]"
                      @click="mineSentence(group.word, sentence)"
                    />
                  </div>
                </div>
              </li>
            </ul>
            <div v-if="mineSentences.length < mineTotal" class="flex justify-center pt-3">
              <Button label="Load more" severity="secondary" outlined size="small" :loading="mineLoading" @click="loadMineSentences()" />
            </div>
          </template>
        </template>
      </Card>
    </template>

    <MiningCardSettingsDialog v-model:visible="cardSettingsVisible" />

    <LazySrsAddDeckDialog
      v-if="studyDialog && mainDeck"
      :visible="true"
      :edit-deck="studyDialog.editDeck"
      :preselected-deck="studyDialog.editDeck ? undefined : { deckId: mainDeck.deckId, originalTitle: mainDeck.originalTitle, coverName: mainDeck.coverName }"
      :initial-order="DeckOrder.SentenceUnlock"
      @update:visible="(visible: boolean) => !visible && (studyDialog = null)"
    />
  </div>
</template>
