<script setup lang="ts">
  import Button from 'primevue/button';
  import Card from 'primevue/card';
  import Tag from 'primevue/tag';
  import { useToast } from 'primevue/usetoast';
  import { FLAG_REASONS, type FlagReason } from '~/utils/minedImport';
  import type { SentenceFlag } from '~/composables/useSentenceFlags';

  const emit = defineEmits<{ changed: [] }>();

  const { $api } = useNuxtApp();
  const toast = useToast();

  const sentenceFlags = useSentenceFlags();
  const flags = sentenceFlags.flags;
  const editing = ref<SentenceFlag | null>(null);
  const dismissing = ref<number | null>(null);

  async function load() {
    try {
      await sentenceFlags.load();
    } catch (e) {
      toast.add({ severity: 'error', summary: "Couldn't load the flags", detail: extractApiError(e, 'The request failed'), life: 5000 });
    }
  }

  async function dismiss(flag: SentenceFlag) {
    dismissing.value = flag.sentenceFlagId;
    try {
      await $api(`sentence-flags/${flag.sentenceFlagId}/dismiss`, { method: 'POST' });
      emit('changed');
    } catch (e) {
      toast.add({ severity: 'error', summary: "Couldn't dismiss the flag", detail: extractApiError(e, 'The request failed'), life: 5000 });
    } finally {
      dismissing.value = null;
    }
  }

  function reasonLabel(reason: FlagReason) {
    return FLAG_REASONS.find((r) => r.value === reason)!.label;
  }

  onMounted(load);
  defineExpose({ load });
</script>

<template>
  <Card v-if="flags.length" class="shadow-md mb-6">
    <template #title>Flagged sentences ({{ flags.length }})</template>
    <template #content>
      <div class="flex flex-col divide-y divide-surface-200 dark:divide-surface-700">
        <div v-for="f in flags" :key="f.sentenceFlagId" class="py-2 flex items-start justify-between gap-3">
          <div class="min-w-0">
            <NuxtLink v-if="f.hasPage" :to="`/sentences/${f.sentenceId}`" class="hover:underline" lang="ja">{{ f.text }}</NuxtLink>
            <span v-else-if="f.text" lang="ja">{{ f.text }}</span>
            <span v-else class="text-gray-500">This line is no longer available.</span>
            <div class="flex flex-wrap items-center gap-2 mt-1 text-xs text-gray-500">
              <Tag v-if="f.reason" :value="reasonLabel(f.reason)" severity="secondary" />
              <Tag v-else value="Reason needed" severity="warn" />
              <span v-if="f.source">{{ f.source }}</span>
              <span>{{ new Date(f.createdAt).toLocaleDateString() }}</span>
            </div>
            <p v-if="f.note" class="text-sm text-gray-600 dark:text-gray-300 mt-1">{{ f.note }}</p>
          </div>
          <span class="flex items-center gap-1 shrink-0">
            <Button v-if="f.text" label="Fix" icon="pi pi-pencil" size="small" text @click="navigateTo(`/sentences/${f.sentenceId}/fix`)" />
            <Button v-if="f.text" :label="f.reason ? 'Edit' : 'Add reason'" size="small" severity="secondary" text @click="editing = f" />
            <Button label="Dismiss" size="small" severity="secondary" text :loading="dismissing === f.sentenceFlagId" @click="dismiss(f)" />
          </span>
        </div>
      </div>
    </template>
  </Card>

  <SentenceFlagDialog
    v-if="editing"
    :sentence-id="editing.sentenceId"
    :text="editing.text!"
    :reason="editing.reason"
    :note="editing.note"
    @close="editing = null"
  />
</template>
