<script setup lang="ts">
  import Button from 'primevue/button';
  import Dialog from 'primevue/dialog';
  import type { MineChoice } from '~/composables/useMineSentence';

  /** Asks whether a sentence for a word already in Anki joins its card or makes a new note (see useMineSentence). */
  defineProps<{ choice: Readonly<MineChoice> | null }>();
  const emit = defineEmits<{ answer: ['existing' | 'new' | null] }>();
</script>

<template>
  <Dialog :visible="choice != null" modal :header="`${choice?.word.reading} is already in Anki`" :style="{ width: '28rem' }" @update:visible="emit('answer', null)">
    <p class="mb-4">
      Add this sentence to its card, where it joins the sentences shown in turn, or create another note for it?
      <span v-if="(choice?.notes.length ?? 0) > 1" class="text-gray-500 dark:text-gray-400">
        The word is on {{ choice?.notes.length }} notes; the oldest gets it.
      </span>
    </p>
    <div class="flex justify-end gap-2">
      <Button label="Cancel" severity="secondary" text @click="emit('answer', null)" />
      <Button label="Create a new note" severity="secondary" outlined @click="emit('answer', 'new')" />
      <Button label="Add to the existing card" autofocus @click="emit('answer', 'existing')" />
    </div>
  </Dialog>
</template>
