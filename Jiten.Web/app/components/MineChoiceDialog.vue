<script setup lang="ts">
  import Button from 'primevue/button';
  import Dialog from 'primevue/dialog';
  import type { MineChoice } from '~/composables/useMineSentence';

  /** Asks whether a sentence for a word already in Anki joins its card or makes a new note (see useMineSentence). */
  const props = defineProps<{ choice: Readonly<MineChoice> | null }>();
  const emit = defineEmits<{ answer: ['existing' | 'new' | null] }>();

  const newLabel = computed(() => {
    const t = props.choice?.target;
    return t?.modelName && t.deckName ? `New ${t.modelName} note in ${t.deckName}` : 'Create a new note';
  });
</script>

<template>
  <Dialog :visible="choice != null" modal :header="`${choice?.word.reading} is already in Anki`" :style="{ width: '32rem', maxWidth: '95vw' }" @update:visible="emit('answer', null)">
    <p class="mb-4">
      <template v-if="choice?.modelName">It has a <b>{{ choice.modelName }}</b> card. </template>
      Add this sentence to it, where it joins the sentences shown in turn, or create a new note?
      <span v-if="(choice?.notes.length ?? 0) > 1" class="text-gray-500 dark:text-gray-400">
        The word is on {{ choice?.notes.length }} notes; the oldest gets it.
      </span>
    </p>
    <div class="flex flex-wrap justify-end gap-2">
      <Button label="Cancel" severity="secondary" text @click="emit('answer', null)" />
      <Button :label="newLabel" severity="secondary" outlined @click="emit('answer', 'new')" />
      <Button :label="choice?.modelName ? `Add to the ${choice.modelName} card` : 'Add to the existing card'" autofocus @click="emit('answer', 'existing')" />
    </div>
  </Dialog>
</template>
