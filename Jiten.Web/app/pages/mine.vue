<script setup lang="ts">
  import type { ExampleSentence, WordSummary } from '~/types';
  import { useAuthStore } from '~/stores/authStore';

  definePageMeta({ middleware: ['auth'] });
  useHead({ title: 'Mine - Jiten' });
  useRobotsRule({ noindex: true, follow: false });

  interface MineEpisode {
    deckId: number;
    order: number;
    title: string;
  }
  interface MineTitle {
    deckId: number;
    title: string;
    englishTitle: string | null;
    mediaType: number;
    episodes: MineEpisode[];
  }
  interface FrequencyDictionary {
    id: number;
    title: string;
  }
  interface MineItem {
    word: WordSummary;
    rank: number | null;
    wordType: string;
    status: string;
    wordSentences: number;
    sentences: ExampleSentence[];
  }
  interface MineResponse {
    items: MineItem[];
    total: number;
    totalWords: number;
    totalSentences: number;
    checked: number;
    rankSource: string;
  }

  const route = useRoute();
  const router = useRouter();
  const auth = useAuthStore();
  const toast = useToast();
  const { $api } = useNuxtApp();
  const { hasFeature, fetched } = useJitenPlus();
  const granted = computed(() => auth.isAuthenticated && fetched.value && hasFeature('sentence-stats'));

  // ---- Filters, kept in the URL so a view can be bookmarked ----
  const q = route.query;
  const str = (v: unknown) => (typeof v === 'string' && v.length > 0 ? v : undefined);
  const num = (v: unknown) => (str(v) != null && !Number.isNaN(Number(v)) ? Number(v) : null);
  /** A word count from the URL, kept to the 0-5 the API accepts. */
  const count = (v: unknown) => {
    const n = num(v);
    return n == null ? null : Math.min(5, Math.max(0, Math.round(n)));
  };
  const ids = (v: unknown) =>
    (str(v) ?? '')
      .split(',')
      .filter(Boolean)
      .map(Number)
      .filter((n) => !Number.isNaN(n));

  const sortOptions = [
    { label: 'Frequency', value: 'frequency' },
    { label: 'Sentences per word', value: 'unlocks' },
    { label: 'Sentence length', value: 'length' },
    { label: 'Difficulty', value: 'difficulty' },
    { label: 'Order in the titles', value: 'order' },
  ];
  const layoutOptions = [
    { label: 'By word', value: 'word' },
    { label: 'By sentence', value: 'sentence' },
  ];
  const scriptOptions = [
    { label: 'Any', value: 'any' },
    { label: 'Kana only', value: 'kana' },
    { label: 'With kanji', value: 'kanji' },
  ];
  const wordTypeOptions = [
    { label: 'Verb', value: 'verb' },
    { label: 'i-adjective', value: 'i-adjective' },
    { label: 'na-adjective', value: 'na-adjective' },
    { label: 'Noun', value: 'noun' },
    { label: 'Adverb', value: 'adverb' },
    { label: 'Expression', value: 'expression' },
    { label: 'Other', value: 'other' },
  ];

  const statusOptions = [
    { label: 'New', value: 'new' },
    { label: 'Learning', value: 'learning' },
    { label: 'Young', value: 'young' },
    { label: 'Mature', value: 'mature' },
  ];
  const defaultStatuses = ['new', 'learning'];
  const othersOptions = [
    { label: 'Young or better', value: 'young' },
    { label: 'Mature only', value: 'mature' },
  ];

  const filters = reactive({
    titles: ids(q.titles),
    episodes: ids(q.episodes),
    dictionary: num(q.dict) as number | null,
    sort: str(q.sort) ?? 'frequency',
    descending: q.desc === '1',
    layout: str(q.layout) ?? 'word',
    frequencyMin: num(q.fmin),
    frequencyMax: num(q.fmax),
    hideUnranked: q.ranked === '1',
    lengthMin: num(q.lmin),
    lengthMax: num(q.lmax),
    statuses: str(q.status)?.split(',').filter(Boolean) ?? [...defaultStatuses],
    othersKnownAt: str(q.others) ?? 'young',
    wordTypes: (str(q.types) ?? '').split(',').filter(Boolean),
    script: str(q.script) ?? 'any',
    requireEnglish: q.en === '1',
    requireAudio: q.audio === '1',
    hideInAnki: q.anki === '1',
    unlearnedMin: count(q.umin),
    unlearnedMax: count(q.umax),
    learningMin: count(q.lrmin),
    learningMax: count(q.lrmax),
    deck: str(q.deck) ?? (null as string | null),
  });
  /** Any count set lifts the one-unknown-word rule; the server then counts New and Learning words, the target included. */
  const countsFiltered = computed(() => [filters.unlearnedMin, filters.unlearnedMax, filters.learningMin, filters.learningMax].some((c) => c != null));

  function filterQuery() {
    const f = filters;
    const entries: Record<string, string | undefined> = {
      titles: f.titles.join(',') || undefined,
      episodes: f.episodes.join(',') || undefined,
      dict: f.dictionary?.toString(),
      sort: f.sort === 'frequency' ? undefined : f.sort,
      desc: f.descending ? '1' : undefined,
      layout: f.layout === 'word' ? undefined : f.layout,
      fmin: f.frequencyMin?.toString(),
      fmax: f.frequencyMax?.toString(),
      ranked: f.hideUnranked ? '1' : undefined,
      lmin: f.lengthMin?.toString(),
      lmax: f.lengthMax?.toString(),
      status: [...f.statuses].sort().join(',') === [...defaultStatuses].sort().join(',') ? undefined : f.statuses.join(',') || undefined,
      others: f.othersKnownAt === 'young' ? undefined : f.othersKnownAt,
      types: f.wordTypes.join(',') || undefined,
      script: f.script === 'any' ? undefined : f.script,
      en: f.requireEnglish ? '1' : undefined,
      audio: f.requireAudio ? '1' : undefined,
      anki: f.hideInAnki ? '1' : undefined,
      umin: f.unlearnedMin?.toString(),
      umax: f.unlearnedMax?.toString(),
      lrmin: f.learningMin?.toString(),
      lrmax: f.learningMax?.toString(),
      deck: f.deck ?? undefined,
    };
    return Object.fromEntries(Object.entries(entries).filter(([, v]) => v != null)) as Record<string, string>;
  }

  function resetFilters() {
    Object.assign(filters, {
      titles: [],
      episodes: [],
      sort: 'frequency',
      descending: false,
      frequencyMin: null,
      frequencyMax: null,
      hideUnranked: false,
      lengthMin: null,
      lengthMax: null,
      statuses: [...defaultStatuses],
      othersKnownAt: 'young',
      wordTypes: [],
      script: 'any',
      requireEnglish: false,
      requireAudio: false,
      hideInAnki: false,
      unlearnedMin: null,
      unlearnedMax: null,
      learningMin: null,
      learningMax: null,
      deck: null,
    });
  }

  // ---- Titles and dictionaries ----
  const titles = ref<MineTitle[]>([]);
  const dictionaries = ref<FrequencyDictionary[]>([]);
  const titleOptions = computed(() => titles.value.map((t) => ({ label: t.englishTitle ? `${t.englishTitle} (${t.title})` : t.title, value: t.deckId })));
  const episodeGroups = computed(() =>
    titles.value
      .filter((t) => filters.titles.includes(t.deckId) && t.episodes.length > 1)
      .map((t) => ({ label: t.englishTitle ?? t.title, items: t.episodes.map((e) => ({ label: `${e.order}. ${e.title}`, value: e.deckId })) }))
  );
  const dictionaryOptions = computed(() => [...dictionaries.value.map((d) => ({ label: d.title, value: d.id })), { label: 'Jiten frequency', value: null }]);

  // Episodes only narrow their own title; a title with none picked is mined whole.
  const requestDeckIds = computed(() => {
    const picked = new Set(filters.episodes);
    const result: number[] = [];
    for (const title of titles.value.filter((t) => filters.titles.includes(t.deckId))) {
      const episodes = title.episodes.filter((e) => picked.has(e.deckId)).map((e) => e.deckId);
      result.push(...(episodes.length > 0 ? episodes : [title.deckId]));
    }
    return result;
  });

  async function loadOptions() {
    const options = await $api<{ titles: MineTitle[]; dictionaries: FrequencyDictionary[] }>('mining/options');
    titles.value = options.titles;
    dictionaries.value = options.dictionaries;
    // First visit: rank by the first imported dictionary (the user's Yomitan one) rather than Jiten's.
    if (!('dict' in route.query) && filters.dictionary == null && options.dictionaries.length > 0) filters.dictionary = options.dictionaries[0]!.id;
  }

  // ---- Words already in Anki (read through the user's AnkiConnect) ----
  const { fetchAudio, wordsInAnki, deckNames, wordsInDeck } = useMiningCards();
  const { miningSetups, load: loadSetups } = useAnkiSetups();
  const ankiWords = ref<string[] | null>(null);
  const ankiLoading = ref(false);

  async function loadAnkiWords(force = false) {
    if ((ankiWords.value && !force) || ankiLoading.value) return;
    await loadSetups().catch(() => {});
    if (!miningSetups.value.length) {
      toast.add({ severity: 'info', summary: 'Set up your mining note types in Anki settings first.', life: 4000, group: 'bottom' });
      filters.hideInAnki = false;
      return;
    }
    ankiLoading.value = true;
    try {
      ankiWords.value = await wordsInAnki();
    } catch {
      toast.add({ severity: 'warn', summary: "Couldn't reach AnkiConnect, so words in Anki aren't hidden.", life: 5000, group: 'bottom' });
      filters.hideInAnki = false;
    } finally {
      ankiLoading.value = false;
    }
  }

  // ---- Only words in one Anki deck ----
  const decks = ref<string[]>([]);
  const decksLoading = ref(false);
  const deckWords = ref<{ deck: string; words: string[] } | null>(null);
  // The read in flight, so a search for the same deck waits on it and a different deck starts its own.
  let deckRead: { deck: string; done: Promise<void> } | null = null;
  const deckLoading = ref(false);
  const deckReady = computed(() => !!filters.deck && deckWords.value?.deck === filters.deck);
  const deckOptions = computed(() => (filters.deck && !decks.value.includes(filters.deck) ? [filters.deck, ...decks.value] : decks.value));

  function ankiUnreachable() {
    toast.add({ severity: 'warn', summary: "Couldn't reach AnkiConnect, so the deck filter is off.", life: 5000, group: 'bottom' });
    filters.deck = null;
  }

  async function loadDecks() {
    if (decks.value.length || decksLoading.value) return;
    decksLoading.value = true;
    try {
      decks.value = await deckNames();
    } catch {
      toast.add({ severity: 'warn', summary: "Couldn't reach AnkiConnect to list your decks.", life: 5000, group: 'bottom' });
    } finally {
      decksLoading.value = false;
    }
  }

  async function loadDeckWords(force = false) {
    const deck = filters.deck;
    if (!deck || (deckWords.value?.deck === deck && !force)) return;
    if (deckRead?.deck === deck && !force) return deckRead.done;
    const read: { deck: string; done: Promise<void> } = { deck, done: Promise.resolve() };
    read.done = (async () => {
      try {
        const words = await wordsInDeck(deck);
        if (deckRead === read) deckWords.value = { deck, words };
      } catch {
        if (deckRead === read && filters.deck === deck) ankiUnreachable();
      } finally {
        if (deckRead === read) {
          deckRead = null;
          deckLoading.value = false;
        }
      }
    })();
    deckRead = read;
    deckLoading.value = true;
    return read.done;
  }

  // ---- Results ----
  const items = ref<MineItem[]>([]);
  const total = ref(0);
  const summary = ref<Omit<MineResponse, 'items'> | null>(null);
  const loading = ref(false);
  const failed = ref(false);
  const expanded = ref<Set<number>>(new Set());
  const showsLearnedWords = computed(() => filters.statuses.some((s) => s === 'young' || s === 'mature'));
  let requestId = 0;

  async function search(reset = true) {
    if (!granted.value) return;
    if (filters.hideInAnki) await loadAnkiWords();
    if (filters.deck) {
      await loadDeckWords();
      // The deck changed or couldn't be read meanwhile; the search that change started will run.
      if (!deckReady.value) return;
    }
    const id = ++requestId;
    loading.value = true;
    failed.value = false;
    try {
      const response = await $api<MineResponse>('mining/sentences', {
        method: 'POST',
        body: {
          deckIds: requestDeckIds.value,
          dictionaryId: filters.dictionary,
          sort: filters.sort,
          descending: filters.descending,
          frequencyMin: filters.frequencyMin,
          frequencyMax: filters.frequencyMax,
          hideUnranked: filters.hideUnranked,
          lengthMin: filters.lengthMin,
          lengthMax: filters.lengthMax,
          requireEnglish: filters.requireEnglish,
          requireAudio: filters.requireAudio,
          statuses: filters.statuses,
          othersKnownAt: filters.othersKnownAt,
          wordTypes: filters.wordTypes,
          script: filters.script === 'any' ? null : filters.script,
          excludeTerms: filters.hideInAnki ? (ankiWords.value ?? []) : [],
          includeTerms: deckReady.value ? deckWords.value!.words : null,
          unlearnedMin: filters.unlearnedMin,
          unlearnedMax: filters.unlearnedMax,
          learningMin: filters.learningMin,
          learningMax: filters.learningMax,
          layout: filters.layout,
          offset: reset ? 0 : items.value.length,
        },
      });
      if (id !== requestId) return;
      items.value = reset ? response.items : [...items.value, ...response.items];
      if (reset) expanded.value = new Set();
      const { items: _, ...rest } = response;
      summary.value = rest;
      total.value = response.total;
    } catch {
      if (id === requestId) failed.value = true;
    } finally {
      if (id === requestId) loading.value = false;
    }
  }

  function toggleExpanded(index: number) {
    const next = new Set(expanded.value);
    if (next.has(index)) next.delete(index);
    else next.add(index);
    expanded.value = next;
  }

  function wordKey(word: WordSummary) {
    return `${word.wordId}-${word.readingIndex}`;
  }

  // ---- Mining: same flow as a title's Sentences page ----
  const cardSettingsVisible = ref(false);
  // By word and sentence: with young or mature targets one sentence can be listed for several words.
  const minedWords = ref<Set<string>>(new Set());
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

  /** A sentence of the word went to Anki through the row's sentence button. */
  function onMined(word: WordSummary) {
    minedWords.value = new Set(minedWords.value).add(wordKey(word));
    ankiWords.value?.push(word.reading);
  }

  // ---- English ----
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

  function englishTitle(sentenceId: number): string {
    const t = translations.value[sentenceId];
    if (!t) return '';
    return t.origin === 'subtitles' ? `From ${t.source ?? 'English subtitles'}` : `AI translation (${t.model ?? t.provider})`;
  }

  watch(items, async (list) => {
    const o = await loadTranslationOptions();
    if (o?.pageMode !== 'auto') return;
    const sentenceIds = list.flatMap((i) => i.sentences.map((s) => s.sentenceId)).filter((id) => !(id in translations.value));
    if (sentenceIds.length > 0) await translateMany(sentenceIds).catch(() => {});
  });

  onBeforeUnmount(() => {
    player?.pause();
    for (const url of audioUrls.values()) URL.revokeObjectURL(url);
  });

  if (import.meta.client) {
    let debounce: ReturnType<typeof setTimeout> | undefined;
    watch(
      () => JSON.stringify(filterQuery()),
      () => {
        router.replace({ query: filterQuery() });
        clearTimeout(debounce);
        debounce = setTimeout(() => search(true), 350);
      }
    );
    // Picking fewer titles drops episodes that no longer belong to one.
    watch(
      () => filters.titles,
      () => {
        const allowed = new Set(titles.value.filter((t) => filters.titles.includes(t.deckId)).flatMap((t) => t.episodes.map((e) => e.deckId)));
        if (filters.episodes.some((e) => !allowed.has(e))) filters.episodes = filters.episodes.filter((e) => allowed.has(e));
      }
    );
    watch(
      granted,
      async (ok) => {
        if (!ok) return;
        await loadOptions();
        loadTranslationOptions();
        search(true);
      },
      { immediate: true }
    );
  }
</script>

<template>
  <div class="flex flex-col gap-4">
    <div class="flex flex-wrap items-start justify-between gap-3">
      <div>
        <h1 class="text-2xl font-bold">Mine</h1>
        <p class="text-sm text-gray-600 dark:text-gray-300">Sentences where you know every word but the target well enough, across your titles.</p>
      </div>
      <Button label="Card settings" icon="pi pi-cog" size="small" severity="secondary" outlined @click="cardSettingsVisible = true" />
    </div>

    <Card v-if="fetched && !granted">
      <template #content>
        <p class="text-gray-600 dark:text-gray-300">Mining needs sentence stats (Jiten+).</p>
      </template>
    </Card>

    <template v-else>
      <Card>
        <template #content>
          <div class="grid grid-cols-1 md:grid-cols-2 xl:grid-cols-3 gap-x-4 gap-y-3">
            <label class="flex flex-col gap-1">
              <span class="text-sm font-medium">Titles</span>
              <MultiSelect
                v-model="filters.titles"
                :options="titleOptions"
                option-label="label"
                option-value="value"
                placeholder="All titles"
                display="chip"
                filter
                class="w-full"
              />
            </label>
            <label v-if="episodeGroups.length > 0" class="flex flex-col gap-1">
              <span class="text-sm font-medium">Episodes</span>
              <MultiSelect
                v-model="filters.episodes"
                :options="episodeGroups"
                option-label="label"
                option-value="value"
                option-group-label="label"
                option-group-children="items"
                placeholder="All episodes"
                :max-selected-labels="3"
                filter
                class="w-full"
              />
            </label>
            <label class="flex flex-col gap-1">
              <span class="text-sm font-medium">Frequency from</span>
              <Select v-model="filters.dictionary" :options="dictionaryOptions" option-label="label" option-value="value" class="w-full" />
            </label>
            <div class="flex flex-col gap-1">
              <span class="text-sm font-medium">Sort by</span>
              <div class="flex gap-2">
                <Select v-model="filters.sort" :options="sortOptions" option-label="label" option-value="value" class="flex-1" />
                <Button
                  v-tooltip.top="filters.descending ? 'Reversed' : 'Natural order'"
                  :icon="(filters.sort === 'unlocks') !== filters.descending ? 'pi pi-sort-amount-down' : 'pi pi-sort-amount-up-alt'"
                  severity="secondary"
                  outlined
                  aria-label="Reverse the order"
                  @click="filters.descending = !filters.descending"
                />
              </div>
            </div>
            <div class="flex flex-col gap-1">
              <span class="text-sm font-medium">Frequency rank</span>
              <div class="flex items-center gap-2">
                <InputNumber v-model="filters.frequencyMin" placeholder="From" :min="1" :use-grouping="false" input-class="w-full" class="flex-1" />
                <span class="text-gray-400">–</span>
                <InputNumber v-model="filters.frequencyMax" placeholder="To" :min="1" :use-grouping="false" input-class="w-full" class="flex-1" />
              </div>
              <label class="flex items-center gap-2 text-sm">
                <Checkbox v-model="filters.hideUnranked" binary />
                Hide words without a rank
              </label>
            </div>
            <div class="flex flex-col gap-1">
              <span class="text-sm font-medium">Sentence length (characters)</span>
              <div class="flex items-center gap-2">
                <InputNumber v-model="filters.lengthMin" placeholder="Min" :min="1" :use-grouping="false" input-class="w-full" class="flex-1" />
                <span class="text-gray-400">–</span>
                <InputNumber v-model="filters.lengthMax" placeholder="Max" :min="1" :use-grouping="false" input-class="w-full" class="flex-1" />
              </div>
            </div>
            <div class="flex flex-col gap-1">
              <span v-tooltip.top="'New words in the sentence, the target included. Empty: one word you don\'t know yet.'" class="text-sm font-medium">
                Unlearned words
              </span>
              <div class="flex items-center gap-2">
                <InputNumber v-model="filters.unlearnedMin" placeholder="Min" :min="0" :max="5" :use-grouping="false" input-class="w-full" class="flex-1" />
                <span class="text-gray-400">–</span>
                <InputNumber v-model="filters.unlearnedMax" placeholder="Max" :min="0" :max="5" :use-grouping="false" input-class="w-full" class="flex-1" />
              </div>
            </div>
            <div class="flex flex-col gap-1">
              <span v-tooltip.top="'Words you are learning (due or suspended) in the sentence, the target included.'" class="text-sm font-medium">
                Learning words
              </span>
              <div class="flex items-center gap-2">
                <InputNumber v-model="filters.learningMin" placeholder="Min" :min="0" :max="5" :use-grouping="false" input-class="w-full" class="flex-1" />
                <span class="text-gray-400">–</span>
                <InputNumber v-model="filters.learningMax" placeholder="Max" :min="0" :max="5" :use-grouping="false" input-class="w-full" class="flex-1" />
              </div>
            </div>
            <label class="flex flex-col gap-1">
              <span class="text-sm font-medium">Target word status</span>
              <MultiSelect
                v-model="filters.statuses"
                :options="statusOptions"
                option-label="label"
                option-value="value"
                placeholder="New, Learning"
                :max-selected-labels="4"
                class="w-full"
              />
              <span v-if="showsLearnedWords && filters.hideInAnki" class="text-xs text-gray-500 dark:text-gray-400">
                "Hide words already in Anki" may hide these.
              </span>
            </label>
            <label class="flex flex-col gap-1">
              <span class="text-sm font-medium">Other words must be</span>
              <Select v-model="filters.othersKnownAt" :options="othersOptions" option-label="label" option-value="value" class="w-full" />
            </label>
            <label class="flex flex-col gap-1">
              <span class="text-sm font-medium">Word type</span>
              <MultiSelect
                v-model="filters.wordTypes"
                :options="wordTypeOptions"
                option-label="label"
                option-value="value"
                placeholder="Any"
                :max-selected-labels="3"
                class="w-full"
              />
            </label>
            <div class="flex flex-col gap-1">
              <span class="text-sm font-medium">Written</span>
              <SelectButton v-model="filters.script" :options="scriptOptions" option-label="label" option-value="value" :allow-empty="false" />
            </div>
            <div class="flex flex-col gap-1">
              <span class="text-sm font-medium">In Anki deck</span>
              <div class="flex items-center gap-2">
                <Select
                  v-model="filters.deck"
                  :options="deckOptions"
                  placeholder="Any deck"
                  filter
                  show-clear
                  :loading="decksLoading || deckLoading"
                  class="flex-1 min-w-0"
                  @before-show="loadDecks"
                />
                <Button
                  v-if="deckReady"
                  v-tooltip.top="'Re-read the deck'"
                  icon="pi pi-refresh"
                  size="small"
                  text
                  rounded
                  aria-label="Re-read the deck"
                  @click="loadDeckWords(true).then(() => search(true))"
                />
              </div>
            </div>
            <div class="flex flex-col gap-2 justify-end">
              <label class="flex items-center gap-2 text-sm">
                <Checkbox v-model="filters.requireEnglish" binary />
                Has English
              </label>
              <label class="flex items-center gap-2 text-sm">
                <Checkbox v-model="filters.requireAudio" binary />
                Has audio (linked video)
              </label>
              <label class="flex items-center gap-2 text-sm">
                <Checkbox v-model="filters.hideInAnki" binary />
                <span
                  v-tooltip.top="
                    miningSetups.length
                      ? `Words in your ${miningSetups.map((s) => s.modelName).join(', ')} notes`
                      : 'Uses the note types set up in Anki settings'
                  "
                >
                  Hide words already in Anki
                </span>
                <i v-if="ankiLoading" class="pi pi-spin pi-spinner text-xs" />
                <Button
                  v-if="filters.hideInAnki && ankiWords"
                  v-tooltip.top="'Re-read Anki'"
                  icon="pi pi-refresh"
                  size="small"
                  text
                  rounded
                  aria-label="Re-read Anki"
                  @click="loadAnkiWords(true).then(() => search(true))"
                />
              </label>
            </div>
          </div>
          <div class="flex flex-wrap items-center justify-between gap-3 mt-4">
            <SelectButton v-model="filters.layout" :options="layoutOptions" option-label="label" option-value="value" :allow-empty="false" />
            <Button label="Reset filters" size="small" severity="secondary" text @click="resetFilters" />
          </div>
        </template>
      </Card>

      <Card>
        <template #content>
          <div v-if="items.length === 0 && loading" class="h-24 rounded bg-surface-100 dark:bg-surface-800 animate-pulse" />
          <p v-else-if="failed && items.length === 0" class="text-gray-500 dark:text-gray-400">Couldn't load the sentences just now.</p>
          <p v-else-if="items.length === 0 && summary" class="text-gray-500 dark:text-gray-400">No sentences match these filters.</p>
          <template v-else>
            <p v-if="summary" class="text-sm text-gray-500 dark:text-gray-400 mb-2">
              {{ summary.totalWords.toLocaleString() }} words · {{ summary.totalSentences.toLocaleString() }} sentences · ranks from
              {{ summary.rankSource }}
              <i v-if="loading" class="pi pi-spin pi-spinner text-xs ml-1" />
            </p>
            <ul class="flex flex-col divide-y divide-surface-200 dark:divide-surface-700">
              <li v-for="(item, index) in items" :key="`${wordKey(item.word)}-${item.sentences[0]?.sentenceId}`" class="py-4 first:pt-0 flex flex-col gap-2">
                <div class="flex flex-wrap items-baseline justify-between gap-x-3 gap-y-1">
                  <NuxtLink
                    :to="`/vocabulary/${item.word.wordId}/${item.word.readingIndex}`"
                    class="flex flex-wrap items-baseline gap-x-2 hover:underline"
                    target="_blank"
                  >
                    <span class="text-lg font-semibold text-primary-600 dark:text-primary-400" lang="ja">{{ item.word.reading }}</span>
                    <span v-if="item.word.mainDefinition" class="text-sm text-gray-600 dark:text-gray-300">{{ item.word.mainDefinition }}</span>
                  </NuxtLink>
                  <span class="flex items-center gap-2 text-xs text-gray-500 dark:text-gray-400 tabular-nums whitespace-nowrap">
                    <i v-if="minedWords.has(wordKey(item.word))" v-tooltip.top="'Added to Anki'" class="pi pi-check text-green-600" />
                    <Tag v-if="item.status !== 'new'" :value="item.status" severity="info" class="capitalize" />
                    <Tag :value="item.rank != null ? `#${item.rank.toLocaleString()}` : 'unranked'" severity="secondary" />
                    <span>{{ item.wordType }}</span>
                    <span>· {{ item.wordSentences.toLocaleString() }} {{ item.wordSentences === 1 ? 'sentence' : 'sentences' }}</span>
                  </span>
                </div>
                <div
                  v-for="sentence in filters.layout === 'word' && !expanded.has(index) ? item.sentences.slice(0, 1) : item.sentences"
                  :key="sentence.sentenceId"
                  class="flex items-start gap-2"
                >
                  <div class="min-w-0 flex-1">
                    <Tag
                      v-if="countsFiltered && sentence.newCount != null"
                      :value="`${sentence.newCount} new · ${sentence.learningCount ?? 0} learning`"
                      severity="secondary"
                      class="text-xs mb-1"
                    />
                    <ExampleSentenceEntry
                      :example-sentence="sentence"
                      :word-id="item.word.wordId"
                      :reading-index="item.word.readingIndex"
                      show-source
                      :show-tts="false"
                      :show-mine="false"
                    />
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
                    <MineSentenceButton
                      boxed
                      page="mine"
                      :sentence="sentence"
                      :word-id="item.word.wordId"
                      :reading-index="item.word.readingIndex"
                      @mined="onMined"
                    />
                  </div>
                </div>
                <Button
                  v-if="filters.layout === 'word' && item.sentences.length > 1"
                  :label="
                    expanded.has(index) ? 'Fewer sentences' : `${item.sentences.length - 1} more ${item.sentences.length === 2 ? 'sentence' : 'sentences'}`
                  "
                  size="small"
                  text
                  class="self-start"
                  @click="toggleExpanded(index)"
                />
              </li>
            </ul>
            <div v-if="items.length < total" class="flex justify-center pt-3">
              <Button label="Load more" severity="secondary" outlined size="small" :loading="loading" @click="search(false)" />
            </div>
          </template>
        </template>
      </Card>
    </template>

    <MiningCardSettingsDialog v-model:visible="cardSettingsVisible" />
  </div>
</template>
