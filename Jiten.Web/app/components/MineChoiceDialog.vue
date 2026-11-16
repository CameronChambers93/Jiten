<script setup lang="ts">
  import Button from 'primevue/button';
  import Dialog from 'primevue/dialog';
  import Tag from 'primevue/tag';
  import type { DeepReadonly } from 'vue';
  import type { MineChoice } from '~/composables/useMineSentence';

  /**
   * Asks whether a sentence for a word already in Anki joins its card or makes a new note (see useMineSentence). With
   * `choice.other`, the card is another entry's of the same spelling, and the two meanings are shown side by side.
   */
  const props = defineProps<{ choice: DeepReadonly<MineChoice> | null }>();
  const emit = defineEmits<{ answer: ['existing' | 'new' | null] }>();

  const newLabel = computed(() => {
    const t = props.choice?.target;
    return t?.modelName && t.deckName ? `New ${t.modelName} note in ${t.deckName}` : 'Create a new note';
  });

  const header = computed(() => {
    const c = props.choice;
    if (!c) return '';
    return c.other ? `${c.word.reading} is in Anki as another word` : `${c.word.reading} is already in Anki`;
  });
</script>

<template>
  <Dialog :visible="choice != null" modal :header="header" :style="{ width: '32rem', maxWidth: '95vw' }" @update:visible="emit('answer', null)">
    <template v-if="choice?.other">
      <dl class="grid grid-cols-[auto_1fr] gap-x-3 gap-y-2 mb-3 text-sm">
        <dt class="text-gray-500 dark:text-gray-400">This line's word</dt>
        <dd>
          <span lang="ja" class="font-medium">{{ choice.word.reading }}</span>
          <span class="ml-2">{{ choice.word.mainDefinition ?? '' }}</span>
        </dd>
        <dt class="text-gray-500 dark:text-gray-400">Card in Anki</dt>
        <dd>
          <span lang="ja" class="font-medium">{{ choice.other.expression }}</span>
          <span class="ml-2">{{ choice.other.meaning ?? 'no dictionary entry' }}</span>
          <span v-if="choice.other.modelName" class="ml-2 text-gray-500 dark:text-gray-400">{{ choice.other.modelName }}</span>
          <Tag v-if="choice.other.wordMatch === 'guessed'" value="Matched by guess" severity="warn" class="ml-2 !text-xs" />
        </dd>
      </dl>
      <p class="mb-4">
        If the line's word was picked wrong, add the sentence to that card.
        <span v-if="(choice.moreOthers ?? 0) > 0" class="text-gray-500 dark:text-gray-400">
          {{ choice.moreOthers }} more {{ choice.moreOthers === 1 ? 'note has' : 'notes have' }} this spelling; this is the oldest.
        </span>
      </p>
    </template>
    <p v-else class="mb-4">
      <template v-if="choice?.modelName">It has a <b>{{ choice.modelName }}</b> card. </template>
      Add this sentence to it, where it joins the sentences shown in turn, or create a new note?
      <span v-if="(choice?.notes.length ?? 0) > 1" class="text-gray-500 dark:text-gray-400">
        The word is on {{ choice?.notes.length }} notes; the oldest gets it.
      </span>
    </p>
    <div class="flex flex-wrap justify-end gap-2">
      <Button label="Cancel" severity="secondary" text @click="emit('answer', null)" />
      <Button v-if="choice?.target" :label="newLabel" severity="secondary" outlined @click="emit('answer', 'new')" />
      <Button :label="choice?.modelName ? `Add to the ${choice.modelName} card` : 'Add to the existing card'" autofocus @click="emit('answer', 'existing')" />
    </div>
  </Dialog>
</template>
