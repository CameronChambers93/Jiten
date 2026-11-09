<script setup lang="ts">
  import Button from 'primevue/button';
  import Card from 'primevue/card';
  import Tag from 'primevue/tag';
  import { useToast } from 'primevue/usetoast';
  import { FLAG_REASONS, type FlagReason } from '~/utils/minedImport';
  import type { ApprovedFix, FixParts, SentenceFlag } from '~/composables/useSentenceFlags';

  const emit = defineEmits<{ changed: [] }>();

  const { $api } = useNuxtApp();
  const toast = useToast();
  const { forget: forgetEnglish } = useSentenceTranslation();

  const sentenceFlags = useSentenceFlags();
  const flags = sentenceFlags.flags;
  const approved = sentenceFlags.approved;
  const editing = ref<SentenceFlag | null>(null);
  const dismissing = ref<number | null>(null);
  const answering = ref<number | null>(null);

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

  /** "A fix was approved for a line you fixed": keep the user's own fix, or drop it for the approved parts. */
  async function answer(item: ApprovedFix, use: boolean) {
    answering.value = item.sentenceId;
    try {
      await $api(`sentences/${item.sentenceId}/fix/approved`, { method: 'POST', body: { use } });
      if (use) forgetEnglish(item.sentenceId);
      await sentenceFlags.load();
      emit('changed');
    } catch (e) {
      toast.add({ severity: 'error', summary: "Couldn't save your choice", detail: extractApiError(e, 'The request failed'), life: 5000 });
    } finally {
      answering.value = null;
    }
  }

  /** The parts that differ, as label, yours and approved. */
  function differences(item: ApprovedFix) {
    const rows: { label: string; mine: string; approved: string }[] = [];
    const { mine, approved: a } = item;
    if (mine.text != null) rows.push({ label: 'Japanese', mine: mine.text, approved: a.text ?? '–' });
    if (mine.english != null) rows.push({ label: 'English', mine: mine.english, approved: a.english ?? '–' });
    if (mine.startMs != null) rows.push({ label: 'Clip', mine: clip(mine), approved: clip(a) });
    if (mine.pictureMs != null) rows.push({ label: 'Frame', mine: formatTime(mine.pictureMs), approved: formatTime(a.pictureMs) });
    return rows;
  }
  const clip = (p: FixParts) => (p.startMs == null ? '–' : `${formatTime(p.startMs)}–${formatTime(p.endMs)}`);
  function formatTime(ms: number | null) {
    if (ms == null) return 'middle of the clip';
    const seconds = ms / 1000;
    return `${Math.floor(seconds / 60)}:${(seconds % 60).toFixed(1).padStart(4, '0')}`;
  }

  function reasonLabel(reason: FlagReason) {
    return FLAG_REASONS.find((r) => r.value === reason)!.label;
  }

  onMounted(load);
  defineExpose({ load });
</script>

<template>
  <Card v-if="flags.length || approved.length" class="shadow-md mb-6">
    <template #title>Flagged sentences ({{ flags.length + approved.length }})</template>
    <template #content>
      <div class="flex flex-col divide-y divide-surface-200 dark:divide-surface-700">
        <div v-for="a in approved" :key="`approved-${a.sentenceId}`" class="py-2 flex flex-col sm:flex-row sm:items-start sm:justify-between gap-3">
          <div class="min-w-0">
            <NuxtLink v-if="a.hasPage" :to="`/sentences/${a.sentenceId}`" class="hover:underline" lang="ja">{{ a.text }}</NuxtLink>
            <span v-else lang="ja">{{ a.text }}</span>
            <div class="flex flex-wrap items-center gap-2 mt-1 text-xs text-gray-500">
              <Tag value="A fix was approved for a line you fixed" />
              <span v-if="a.source">{{ a.source }}</span>
              <span>approved {{ new Date(a.approvedAt).toLocaleDateString() }}</span>
            </div>
            <dl class="mt-1 grid grid-cols-[auto_1fr] gap-x-3 gap-y-0.5 text-sm">
              <template v-for="d in differences(a)" :key="d.label">
                <dt class="text-gray-500">{{ d.label }}</dt>
                <dd class="min-w-0">
                  <div>
                    <span class="text-gray-500">Yours:</span>&#32;<span lang="ja">{{ d.mine }}</span>
                  </div>
                  <div>
                    <span class="text-gray-500">Approved:</span>&#32;<span lang="ja">{{ d.approved }}</span>
                  </div>
                </dd>
              </template>
            </dl>
          </div>
          <span class="flex items-center gap-1 shrink-0 self-start">
            <Button label="Keep mine" size="small" :loading="answering === a.sentenceId" @click="answer(a, false)" />
            <Button label="Use the approved one" size="small" severity="secondary" outlined :disabled="answering === a.sentenceId" @click="answer(a, true)" />
          </span>
        </div>
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
