<script setup lang="ts">
  import Button from 'primevue/button';
  import Dialog from 'primevue/dialog';
  import RadioButton from 'primevue/radiobutton';
  import Textarea from 'primevue/textarea';
  import { useToast } from 'primevue/usetoast';
  import { FLAG_REASONS, type FlagReason } from '~/utils/minedImport';

  /** Flags a sentence with a reason and an optional note; on a sentence already flagged, replaces them. */
  const props = defineProps<{ sentenceId: number; text: string; reason?: FlagReason | null; note?: string | null }>();
  const emit = defineEmits<{ close: []; saved: [] }>();

  const { $api } = useNuxtApp();
  const toast = useToast();
  const sentenceFlags = useSentenceFlags();

  const reason = ref<FlagReason | null>(props.reason ?? null);
  const note = ref(props.note ?? '');
  const saving = ref(false);

  async function save() {
    saving.value = true;
    try {
      await $api('sentence-flags', { method: 'POST', body: { sentenceId: props.sentenceId, reason: reason.value, note: note.value } });
      // Every sentence on the page shows the new state; a failed reload only leaves the icons stale.
      await sentenceFlags.load().catch(() => {});
      emit('saved');
      emit('close');
    } catch (e) {
      toast.add({ severity: 'error', summary: "Couldn't flag the sentence", detail: extractApiError(e, 'The flag failed'), life: 5000 });
    } finally {
      saving.value = false;
    }
  }
</script>

<template>
  <Dialog visible modal header="Flag this sentence" :style="{ width: '28rem' }" @update:visible="emit('close')">
    <!-- eslint-disable-next-line vue/no-v-html -->
    <p class="mb-3" lang="ja" v-html="sanitiseHtml(text)" />
    <div class="flex flex-col gap-2 mb-3">
      <label v-for="r in FLAG_REASONS" :key="r.value" class="flex items-center gap-2">
        <RadioButton v-model="reason" :value="r.value" />
        {{ r.label }}
      </label>
    </div>
    <Textarea v-model="note" rows="2" maxlength="1000" class="w-full" placeholder="Note (optional)" aria-label="Note" />
    <p class="text-xs text-gray-500 mt-2">Only you stop seeing it: it's hidden from Mine and, after the next Sync, from your cards until it's fixed or dismissed.</p>
    <div class="flex justify-end gap-2 mt-3">
      <Button label="Cancel" severity="secondary" text @click="emit('close')" />
      <Button label="Save" :disabled="!reason" :loading="saving" @click="save" />
    </div>
  </Dialog>
</template>
