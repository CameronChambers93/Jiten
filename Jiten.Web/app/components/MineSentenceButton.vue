<script setup lang="ts">
  import Button from 'primevue/button';
  import Dialog from 'primevue/dialog';
  import { useToast } from 'primevue/usetoast';
  import type { ExampleSentence, WordSummary } from '~/types';

  /**
   * Add to Anki for any example sentence (n1miner). The card is for the given word, or for one picked from the sentence.
   * When the word already has a note, the sentence can join that card instead of making another.
   */
  const props = defineProps<{
    sentence: ExampleSentence;
    /** The sentence's own word (its wordPosition/wordLength); without it the user picks one. */
    wordId?: number;
    readingIndex?: number;
  }>();

  interface ParsedWord {
    wordId: number;
    readingIndex: number;
    originalText: string;
  }

  const { $api } = useNuxtApp();
  const toast = useToast();
  const { configured } = useMiningCards();
  const { mine: mineSentence, choice, answer } = useMineSentence();

  const state = ref<'idle' | 'working' | 'added'>('idle');
  const settingsVisible = ref(false);
  const picking = ref<{ word: ParsedWord; position: number }[] | null>(null);

  function failed(e: unknown, summary: string) {
    const err = e as { data?: { message?: string }; message?: string } | null;
    const detail = err?.data?.message ?? err?.message;
    toast.add({ severity: 'error', summary, detail, life: 6000, group: 'bottom' });
  }

  async function summaryOf(wordId: number, readingIndex: number): Promise<WordSummary> {
    const { words } = await $api<{ words: WordSummary[] }>('mined/word-summaries', { query: { words: `${wordId}:${readingIndex}` } });
    if (!words[0]) throw new Error('Jiten has no reading for that word.');
    return words[0];
  }

  /** The sentence's words in order, where each sits in its text. */
  async function sentenceWords(): Promise<{ word: ParsedWord; position: number }[]> {
    const parsed = await $api<ParsedWord[]>('vocabulary/parse', { query: { text: props.sentence.text } });
    const words: { word: ParsedWord; position: number }[] = [];
    let cursor = 0;
    for (const word of parsed) {
      const at = props.sentence.text.indexOf(word.originalText, cursor);
      if (at < 0) continue;
      cursor = at + word.originalText.length;
      if (word.wordId > 0) words.push({ word, position: at });
    }
    return words;
  }

  async function start() {
    if (!configured.value) {
      settingsVisible.value = true;
      return;
    }
    const { wordId, readingIndex } = props;
    if (wordId != null && readingIndex != null && props.sentence.wordLength > 0) return mine(wordId, readingIndex, props.sentence.wordPosition, props.sentence.wordLength);

    state.value = 'working';
    try {
      picking.value = await sentenceWords();
    } catch (e) {
      failed(e, "Couldn't read the sentence's words");
    } finally {
      state.value = 'idle';
    }
  }

  async function mine(wordId: number, readingIndex: number, position: number, length: number) {
    picking.value = null;
    state.value = 'working';
    let word: WordSummary;
    try {
      word = await summaryOf(wordId, readingIndex);
    } catch (e) {
      state.value = 'idle';
      failed(e, "Couldn't add the card");
      return;
    }
    const outcome = await mineSentence(word, { ...props.sentence, wordPosition: position, wordLength: length });
    state.value = outcome ? 'added' : 'idle';
  }
</script>

<template>
  <button
    class="inline-flex items-center justify-center transition-colors"
    :class="state === 'added' ? 'text-green-500' : 'text-surface-400 hover:text-primary-500 cursor-pointer'"
    :disabled="state !== 'idle'"
    :title="state === 'added' ? 'Added to Anki' : 'Add to Anki'"
    @click="start"
  >
    <i class="pi text-sm" :class="state === 'working' ? 'pi-spin pi-spinner' : state === 'added' ? 'pi-check' : 'pi-plus-circle'" />
  </button>

  <Dialog :visible="picking != null" modal header="Which word is the card for?" :style="{ width: '28rem' }" @update:visible="picking = null">
    <p lang="ja" class="mb-3">{{ sentence.text }}</p>
    <div v-if="picking?.length" class="flex flex-wrap gap-2" lang="ja">
      <Button
        v-for="item in picking"
        :key="`${item.position}-${item.word.wordId}`"
        :label="item.word.originalText"
        severity="secondary"
        outlined
        size="small"
        @click="mine(item.word.wordId, item.word.readingIndex, item.position, item.word.originalText.length)"
      />
    </div>
    <p v-else class="text-gray-500 dark:text-gray-400">Jiten found no dictionary words in this sentence.</p>
  </Dialog>

  <MineChoiceDialog :choice="choice" @answer="answer" />

  <MiningCardSettingsDialog v-model:visible="settingsVisible" />
</template>
