<script setup lang="ts">
  import type { ExampleSentence, WordSummary } from '~/types';

  definePageMeta({ middleware: ['auth'] });
  useHead({ title: 'Sentence - Jiten' });
  useRobotsRule({ noindex: true, follow: false });

  interface SentenceDetailWord {
    word: WordSummary;
    status: string;
    position: number;
    length: number;
  }
  interface SentenceContextLine {
    startMs: number;
    text: string;
  }
  /** A line of the sentence's clip: the sentence itself, or other speech its audio holds. */
  interface SentenceClipLine {
    startMs: number;
    text: string;
    isSentence: boolean;
    sentenceId: number | null;
  }
  interface SentenceDetail {
    sentence: ExampleSentence;
    words: SentenceDetailWord[];
    before: SentenceContextLine[];
    /** Empty when the clip holds only the sentence. */
    clip: SentenceClipLine[];
    after: SentenceContextLine[];
    elsewhere: ExampleSentence[];
  }

  const route = useRoute();
  const toast = useToast();
  const { $api } = useNuxtApp();
  const sentenceId = computed(() => Number(route.params.id));

  const detail = ref<SentenceDetail | null>(null);
  const loading = ref(true);
  const missing = ref(false);

  async function load() {
    // A link to another sentence reuses this page: drop the previous sentence's audio and card states.
    resetAudio();
    cardState.value = {};
    loading.value = true;
    missing.value = false;
    try {
      detail.value = await $api<SentenceDetail>(`sentences/${sentenceId.value}`, {
        query: { wordId: Number(route.query.wordId) || 0, readingIndex: Number(route.query.readingIndex) || 0 },
      });
      loadPicture();
      loadEnglish();
    } catch {
      detail.value = null;
      missing.value = true;
    } finally {
      loading.value = false;
    }
  }

  // ---- Screenshot ----
  const { load: loadSentencePicture } = useSentencePicture();
  const picture = ref<string | null>(null);

  async function loadPicture() {
    picture.value = detail.value?.sentence.hasVideo ? await loadSentencePicture(sentenceId.value) : null;
  }

  // ---- Audio: the linked video, or text-to-speech without one ----
  const { fetchAudio, configured: cardsConfigured } = useMiningCards();
  const { speakSentence, stop: stopTts } = useTts(ref(''), 'sentence');
  const audioLoading = ref(false);
  let player: HTMLAudioElement | null = null;
  let audioUrl: string | null = null;

  function resetAudio() {
    player?.pause();
    player = null;
    if (audioUrl) URL.revokeObjectURL(audioUrl);
    audioUrl = null;
  }

  async function play() {
    const sentence = detail.value?.sentence;
    if (!sentence) return;
    player?.pause();
    stopTts();
    if (!sentence.hasVideo) return speakSentence(sentence.sentenceId, sentence.text);
    if (!audioUrl) {
      audioLoading.value = true;
      try {
        audioUrl = URL.createObjectURL(await fetchAudio(sentence.sentenceId));
      } catch {
        speakSentence(sentence.sentenceId, sentence.text);
        return;
      } finally {
        audioLoading.value = false;
      }
    }
    player = new Audio(audioUrl);
    player.play();
  }

  // ---- English ----
  const { options: translationOptions, translations, loadOptions, translate } = useSentenceTranslation();
  const translating = ref(false);
  const english = computed(() => translations.value[sentenceId.value] ?? null);

  async function loadEnglish() {
    const options = await loadOptions();
    if (options?.pageMode !== 'off') await translate(sentenceId.value, false).catch(() => null);
  }

  async function translateWithAi() {
    translating.value = true;
    try {
      await translate(sentenceId.value, true);
    } catch (e) {
      const message = (e as { data?: { message?: string } })?.data?.message;
      toast.add({ severity: 'error', summary: message ?? "Couldn't translate the sentence", life: 5000, group: 'bottom' });
    } finally {
      translating.value = false;
    }
  }

  // ---- Mining: the same flow as the Mine page, for any word of the sentence ----
  const cardSettingsVisible = ref(false);
  const { mine, choice: mineChoice, answer: answerMine } = useMineSentence();
  const cardState = ref<Record<string, 'adding' | 'added'>>({});

  async function mineWord({ word, position, length }: SentenceDetailWord) {
    const sentence = detail.value?.sentence;
    if (!sentence) return;
    if (!cardsConfigured.value) {
      cardSettingsVisible.value = true;
      return;
    }
    const key = wordKey(word);
    cardState.value = { ...cardState.value, [key]: 'adding' };
    const outcome = await mine(word, { ...sentence, wordPosition: position, wordLength: length });
    if (outcome) {
      cardState.value = { ...cardState.value, [key]: 'added' };
    } else {
      const { [key]: _, ...rest } = cardState.value;
      cardState.value = rest;
    }
  }

  function wordKey(word: WordSummary) {
    return `${word.wordId}-${word.readingIndex}`;
  }

  const targetWordId = computed(() => Number(route.query.wordId) || 0);

  function formatTime(ms: number) {
    const seconds = Math.floor(ms / 1000);
    return `${Math.floor(seconds / 60)}:${String(seconds % 60).padStart(2, '0')}`;
  }

  onBeforeUnmount(resetAudio);

  if (import.meta.client) watch(() => route.fullPath, load, { immediate: true });
</script>

<template>
  <div class="flex flex-col gap-4 max-w-4xl mx-auto w-full">
    <div v-if="loading && !detail" class="h-48 rounded bg-surface-100 dark:bg-surface-800 animate-pulse" />
    <Card v-else-if="missing">
      <template #content>
        <p class="text-gray-600 dark:text-gray-300">This sentence doesn't exist, or it's private.</p>
      </template>
    </Card>

    <template v-else-if="detail">
      <img v-if="picture" :src="picture" alt="Screenshot of the sentence" class="w-full max-h-[60vh] object-contain rounded bg-black" />

      <Card>
        <template #content>
          <div class="flex flex-col gap-3">
            <p v-for="line in detail.before" :key="`before-${line.startMs}`" class="text-sm text-gray-500 dark:text-gray-400" lang="ja">
              <span class="tabular-nums mr-2">{{ formatTime(line.startMs) }}</span
              >{{ line.text }}
            </p>
            <div v-if="detail.clip.length > 0" class="flex flex-col gap-2 rounded border border-surface-200 dark:border-surface-700 p-3">
              <span class="text-xs font-semibold uppercase tracking-wide text-gray-500 dark:text-gray-400">Clip</span>
              <template v-for="(line, index) in detail.clip" :key="`clip-${index}`">
                <ExampleSentenceEntry
                  v-if="line.isSentence"
                  :example-sentence="detail.sentence"
                  show-source
                  :show-tts="false"
                  :show-mine="false"
                  :show-picture="false"
                  :show-page-link="false"
                />
                <NuxtLink v-else-if="line.sentenceId" :to="`/sentences/${line.sentenceId}`" class="self-start hover:underline">
                  <span class="text-gray-500 dark:text-gray-400" lang="ja">{{ line.text }}</span>
                  <i class="pi pi-arrow-up-right text-xs text-gray-400 ml-1" aria-hidden="true" />
                </NuxtLink>
                <p v-else class="text-gray-500 dark:text-gray-400" lang="ja">{{ line.text }}</p>
              </template>
            </div>
            <ExampleSentenceEntry
              v-else
              :example-sentence="detail.sentence"
              show-source
              :show-tts="false"
              :show-mine="false"
              :show-picture="false"
              :show-page-link="false"
            />
            <p v-for="line in detail.after" :key="`after-${line.startMs}`" class="text-sm text-gray-500 dark:text-gray-400" lang="ja">
              <span class="tabular-nums mr-2">{{ formatTime(line.startMs) }}</span
              >{{ line.text }}
            </p>
            <p v-if="english" class="text-gray-600 dark:text-gray-300">
              {{ english.text }}
              <i v-if="english.origin === 'ai'" class="pi pi-sparkles text-xs text-gray-400" aria-label="AI translation" />
            </p>
            <div class="flex flex-wrap gap-2">
              <Button label="Listen" icon="pi pi-play" size="small" severity="secondary" outlined :loading="audioLoading" @click="play" />
              <Button
                v-if="!english && translationOptions?.aiAvailable && translationOptions.pageMode !== 'off'"
                label="Translate"
                icon="pi pi-language"
                size="small"
                severity="secondary"
                outlined
                :loading="translating"
                @click="translateWithAi"
              />
            </div>
          </div>
        </template>
      </Card>

      <Card>
        <template #title><span class="text-lg">Words</span></template>
        <template #content>
          <p v-if="detail.words.length === 0" class="text-gray-500 dark:text-gray-400">This sentence has no parsed words yet.</p>
          <ul class="flex flex-col divide-y divide-surface-200 dark:divide-surface-700">
            <li v-for="entry in detail.words" :key="wordKey(entry.word)" class="py-2 flex items-center justify-between gap-3">
              <NuxtLink :to="`/vocabulary/${entry.word.wordId}/${entry.word.readingIndex}`" target="_blank" class="min-w-0 hover:underline">
                <span class="font-semibold" :class="{ 'text-primary-600 dark:text-primary-400': entry.word.wordId === targetWordId }" lang="ja">{{
                  entry.word.reading
                }}</span>
                <span v-if="entry.word.mainDefinition" class="text-sm text-gray-600 dark:text-gray-300 ml-2">{{ entry.word.mainDefinition }}</span>
              </NuxtLink>
              <span class="flex items-center gap-2 shrink-0">
                <Tag :value="entry.status" severity="secondary" class="capitalize" />
                <Button
                  v-tooltip.left="cardState[wordKey(entry.word)] === 'added' ? 'Added to Anki' : 'Add to Anki'"
                  :icon="cardState[wordKey(entry.word)] === 'added' ? 'pi pi-check' : 'pi pi-plus'"
                  size="small"
                  :severity="cardState[wordKey(entry.word)] === 'added' ? 'success' : 'secondary'"
                  text
                  rounded
                  :aria-label="`Add ${entry.word.reading} to Anki`"
                  :loading="cardState[wordKey(entry.word)] === 'adding'"
                  :disabled="!!cardState[wordKey(entry.word)]"
                  @click="mineWord(entry)"
                />
              </span>
            </li>
          </ul>
        </template>
      </Card>

      <Card v-if="detail.elsewhere.length > 0">
        <template #title><span class="text-lg">Also appears in</span></template>
        <template #content>
          <div class="flex flex-col gap-2">
            <ExampleSentenceEntry
              v-for="other in detail.elsewhere"
              :key="other.sentenceId"
              :example-sentence="other"
              show-source
              :show-tts="false"
              :show-mine="false"
            />
          </div>
        </template>
      </Card>
    </template>

    <MiningCardSettingsDialog v-model:visible="cardSettingsVisible" />
    <MineChoiceDialog :choice="mineChoice" @answer="answerMine" />
  </div>
</template>
