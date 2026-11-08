<script setup lang="ts">
  import type { ExampleSentence, SentenceFuriganaMode } from '~/types';
  import { sentenceRubyHtml, targetWordId, visibleFurigana } from '~/utils/sentenceRuby';
  import { computed, ref } from 'vue';

  const props = withDefaults(
    defineProps<{
      exampleSentence: ExampleSentence;
      showSource?: boolean;
      showActions?: boolean;
      /** Source and actions hidden by showSource/showActions can be shown per sentence on demand. */
      revealable?: boolean;
      wordId?: number;
      readingIndex?: number;
      // The user already has the max number of custom sentences for this word — disable the create paths.
      atLimit?: boolean;
      // Defaults to the site-wide furigana preference.
      furiganaMode?: SentenceFuriganaMode;
      hiddenWordId?: number;
      /** The sentence's own word is known, so the i+N badge counts from i+0. */
      targetKnown?: boolean;
      /** Off where the page has its own listen button. */
      showTts?: boolean;
      /** The sentence button (save to the word's list, Add to Anki); off where the page has its own. */
      showMine?: boolean;
      /** Thumbnail from the linked video, and a link to the sentence's own page; off on that page. */
      showPicture?: boolean;
      showPageLink?: boolean;
    }>(),
    { showActions: true, showTts: true, showMine: true, showPicture: true, showPageLink: true }
  );

  defineEmits<{
    favourited: [];
  }>();

  const authStore = useAuthStore();
  const localiseTitle = useLocaliseTitle();
  const store = useJitenStore();
  const isNsfw = isTextNsfw(props.exampleSentence.text);
  const revealedLocally = ref(false);
  const isRevealed = computed(() => store.displayAllNsfw || revealedLocally.value);

  const stateHex = useWordStateHex();

  const hasSource = computed(() => props.exampleSentence.sourceDeck != null);
  const canReveal = computed(() => props.revealable && (!props.showActions || (!props.showSource && hasSource.value)));
  const detailsRevealed = ref(false);
  const actionsVisible = computed(() => props.showActions || detailsRevealed.value);
  const sourceVisible = computed(() => props.showSource || detailsRevealed.value);

  const formattedText = computed(() => {
    const { text, wordPosition, wordLength, furigana, words } = props.exampleSentence;
    const setting = props.furiganaMode ?? store.sentenceFurigana;
    // Signed out, every word reads as unknown, so "unknown words only" means all of them.
    const mode = setting === 'unknown' && !authStore.isAuthenticated ? 'all' : setting;
    const hiddenWordId = props.hiddenWordId ?? (mode === 'exceptTarget' ? targetWordId(furigana, wordPosition, wordLength) : undefined);
    const shown = new Set(visibleFurigana(furigana, mode, hiddenWordId));
    const coloured = store.colourWordsByState && authStore.isAuthenticated && !!words?.length;
    const peek = store.furiganaOnHover;
    const unknownSpans = props.exampleSentence.unknownSpans ?? [];
    if (unknownSpans.length > 0 || coloured || (furigana?.length && (shown.size > 0 || peek))) {
      return sanitiseHtml(
        sentenceRubyHtml(text, wordPosition, wordLength, furigana ?? [], {
          unknownSpans,
          showReading: (g) => shown.has(g),
          // The caller's hidden word is the answer being tested, so it never peeks.
          revealOnHover: peek ? (g) => g.wordId !== props.hiddenWordId : undefined,
          words: words ?? undefined,
          colourOf: coloured ? (w) => (w.wordId === props.hiddenWordId ? null : stateHex(w.states)) : undefined,
        })
      );
    }
    if (wordPosition < 0 || wordLength <= 0 || wordPosition >= text.length) {
      return sanitiseHtml(text);
    }

    const before = text.substring(0, wordPosition).trim();
    const bold = text.substring(wordPosition, wordPosition + wordLength);
    const after = text.substring(wordPosition + wordLength).trim();

    const html = before + '<span class="text-primary-500 dark:text-primary-500 font-bold">' + bold + '</span>' + after;
    return sanitiseHtml(html);
  });

  const handleReveal = () => {
    if (isNsfw && !isRevealed.value) {
      revealedLocally.value = true;
    }
  };

  const pictureVisible = computed(() => props.showPicture && authStore.isAuthenticated && !!props.exampleSentence.hasVideo && (!isNsfw || isRevealed.value));
  const pageLink = computed(() => {
    const query = props.wordId != null ? `?wordId=${props.wordId}&readingIndex=${props.readingIndex ?? 0}` : '';
    return `/sentences/${props.exampleSentence.sentenceId}${query}`;
  });

  // Fix opens the fix screen (the pencil used to copy the line into a custom sentence; typed ones keep the heading's pencil).
  const fixLink = computed(() => `/sentences/${props.exampleSentence.sentenceId}/fix`);
  const fixTitle = computed(() =>
    props.exampleSentence.fixed === 'mine' ? 'Your fix: change it' : props.exampleSentence.fixed === 'shared' ? 'Fixed for everyone: fix it again' : 'Fix this sentence'
  );

  const flagging = ref(false);
  const sentenceFlags = useSentenceFlags();
  const flag = computed(() => sentenceFlags.flagFor(props.exampleSentence.sentenceId));
  const flagged = computed(() => flag.value != null);
  onMounted(sentenceFlags.ensureLoaded);
</script>

<template>
  <div class="flex flex-col">
      <div class="flex items-start gap-2">
        <blockquote
          class="relative min-w-0 flex-1 border-l-4 border-primary-500 pl-5 pr-3 py-3 bg-gray-50 dark:bg-gray-900 rounded-r shadow-sm overflow-hidden"
        >
          <div class="float-right ml-2 mt-0.5 flex items-center gap-2">
            <span v-if="exampleSentence.isIPlusOne || exampleSentence.unknownCount != null" class="h-5 inline-flex items-center">
              <IPlusOneBadge :unknown="exampleSentence.unknownCount ?? 0" :target-known="targetKnown" />
            </span>
            <TtsButton v-if="actionsVisible && showTts" :text="exampleSentence.text" :sentence-id="exampleSentence.sentenceId" type="sentence" size="sm" />
            <MineSentenceButton
              v-if="actionsVisible && showMine && authStore.isAuthenticated"
              :sentence="exampleSentence"
              :word-id="wordId"
              :reading-index="readingIndex"
            />
            <NuxtLink
              v-if="actionsVisible && authStore.isAuthenticated && exampleSentence.sentenceId > 0"
              :to="fixLink"
              class="inline-flex items-center justify-center transition-colors"
              :class="exampleSentence.fixed ? 'text-primary-500' : 'text-surface-400 hover:text-primary-500'"
              :title="fixTitle"
              :aria-label="fixTitle"
            >
              <i class="pi pi-pencil text-sm" />
            </NuxtLink>
            <button
              v-if="actionsVisible && authStore.isAuthenticated && exampleSentence.sentenceId > 0"
              class="inline-flex items-center justify-center transition-colors"
              :class="flagged ? 'text-red-500' : 'text-surface-400 hover:text-red-500'"
              :title="flagged ? 'Flagged: hidden from Mine and your cards' : 'Flag this sentence'"
              :aria-label="flagged ? 'Edit this flag' : 'Flag this sentence'"
              @click="flagging = true"
            >
              <i class="pi text-sm" :class="flagged ? 'pi-flag-fill' : 'pi-flag'" />
            </button>
            <NuxtLink
              v-if="showPageLink && actionsVisible && authStore.isAuthenticated && exampleSentence.sentenceId > 0"
              :to="pageLink"
              class="inline-flex items-center justify-center text-surface-400 hover:text-primary-500 transition-colors"
              title="Open this sentence"
              aria-label="Open this sentence"
            >
              <i class="pi pi-arrow-up-right text-sm" />
            </NuxtLink>
            <SentenceDetailsToggle v-if="canReveal" v-model="detailsRevealed" />
          </div>
          <div
            class="transition-filter duration-200"
            lang="ja"
            :class="[sentenceSizeClass(store.sentenceSize), { 'blur-sm': isNsfw && !isRevealed }]"
            @click="handleReveal"
            v-html="formattedText"
          />
          <div
            v-if="isNsfw && !isRevealed"
            class="absolute top-0 left-0 w-full h-full flex items-center justify-center cursor-pointer z-10"
            role="button"
            tabindex="0"
            aria-label="Reveal potentially not safe for work text"
            @click="handleReveal"
            @keydown.enter="handleReveal"
            @keydown.space.prevent="handleReveal"
          >
            <div class="text-center px-3 py-2 bg-white/80 backdrop-blur-md border border-red-300 text-red-600 text-sm font-semibold rounded shadow">
              This text is potentially not safe for work. Click to reveal.
            </div>
          </div>
        </blockquote>
        <SentenceThumbnail
          v-if="pictureVisible"
          :key="`${exampleSentence.sentenceId}${exampleSentence.pictureKey ?? ''}`"
          :sentence-id="exampleSentence.sentenceId"
          :clip-key="exampleSentence.clipKey ?? ''"
          :picture-key="exampleSentence.pictureKey ?? ''"
          class="w-24 sm:w-32 shrink-0"
        />
      </div>
      <div v-if="sourceVisible && hasSource" class="flex items-center mb-2">
        <span class="text-xs italic mr-2 ml-4">Source:</span>
        <div class="inline-flex items-center text-xs flex-wrap">
          <NuxtLink
            v-if="exampleSentence.sourceDeckParent != null"
            :to="`/decks/media/${exampleSentence.sourceDeckParent.deckId}/detail`"
            target="_blank"
            class="hover:underline text-primary-600"
          >
            {{ localiseTitle(exampleSentence.sourceDeckParent) }}
          </NuxtLink>
          <span v-if="exampleSentence.sourceDeckParent != null" class="mx-1">-</span>
          <NuxtLink
            v-if="exampleSentence.sourceDeck != null"
            :to="`/decks/media/${exampleSentence.sourceDeck.deckId}/detail`"
            target="_blank"
            class="hover:underline text-primary-600"
          >
            {{ localiseTitle(exampleSentence.sourceDeck) }}
          </NuxtLink>
          &nbsp; ({{ getMediaTypeText(exampleSentence.sourceDeck.mediaType) }})
        </div>
      </div>
    <SentenceFlagDialog
      v-if="flagging"
      :sentence-id="exampleSentence.sentenceId"
      :text="exampleSentence.text"
      :reason="flag?.reason"
      :note="flag?.note"
      @close="flagging = false"
    />
  </div>
</template>

<style scoped></style>
