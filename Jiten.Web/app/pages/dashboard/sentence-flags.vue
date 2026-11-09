<script setup lang="ts">
  import Button from 'primevue/button';
  import Paginator from 'primevue/paginator';
  import ProgressSpinner from 'primevue/progressspinner';
  import Select from 'primevue/select';
  import Tag from 'primevue/tag';
  import { useToast } from 'primevue/usetoast';
  import { FLAG_REASONS, type FlagReason } from '~/utils/minedImport';

  interface FlagNote {
    reason: FlagReason | null;
    note: string;
    createdAt: string;
  }

  interface FlaggedLine {
    sentenceId: number;
    text: string;
    title: string | null;
    episode: string | null;
    deckId: number | null;
    hasPage: boolean;
    removedFromDeck: boolean;
    flagCount: number;
    /** Open flags per reason; "" counts flags from cards that have none yet. */
    reasons: Record<string, number>;
    notes: FlagNote[];
    firstFlaggedAt: string;
  }

  interface CorrectedTitle {
    deckId: number;
    title: string;
    corrections: number;
  }

  useHead({ title: 'Flagged Sentences - Jiten Admin' });
  definePageMeta({ middleware: ['auth-admin'] });

  const { $api } = useNuxtApp();
  const toast = useToast();

  const items = ref<FlaggedLine[]>([]);
  const totalCount = ref(0);
  const loading = ref(false);
  const reason = ref<FlagReason | null>(null);
  const page = ref(0);
  const rows = 50;
  const fixing = ref<number | null>(null);
  const corrected = ref<CorrectedTitle[]>([]);
  const reparsing = ref(false);
  /** Flagged lines, or fixes users submitted for review. */
  const tab = ref<'flags' | 'submissions'>(useRoute().query.tab === 'submissions' ? 'submissions' : 'flags');
  const pendingSubmissions = ref(0);

  function reasonLabel(value: string) {
    return FLAG_REASONS.find((r) => r.value === value)?.label ?? 'No reason yet';
  }

  async function load() {
    loading.value = true;
    try {
      const params = new URLSearchParams({ offset: String(page.value * rows), limit: String(rows) });
      if (reason.value) params.append('reason', reason.value);
      const result = await $api<{ items: FlaggedLine[]; totalCount: number }>(`admin/sentence-flags?${params}`);
      items.value = result.items;
      totalCount.value = result.totalCount;
    } catch (e) {
      toast.add({ severity: 'error', summary: "Couldn't load the flags", detail: extractApiError(e, 'The request failed'), life: 5000 });
    } finally {
      loading.value = false;
    }
  }

  /** Closes every flag on the line: "fixed" once it was fixed elsewhere, "reject" when nothing was wrong (Not a problem). */
  async function close(line: FlaggedLine, how: 'fixed' | 'reject') {
    fixing.value = line.sentenceId;
    try {
      const { closed } = await $api<{ closed: number }>(`admin/sentence-flags/${line.sentenceId}/${how}`, { method: 'POST' });
      toast.add({
        severity: 'success',
        summary: how === 'fixed' ? 'Marked fixed' : 'Marked not a problem',
        detail: `Closed ${closed} flag${closed === 1 ? '' : 's'}.`,
        life: 3000,
      });
      // The last line on a page leaves it empty: step back.
      if (items.value.length === 1 && page.value > 0) page.value--;
      await load();
    } catch (e) {
      toast.add({ severity: 'error', summary: "Couldn't close the flags", detail: extractApiError(e, 'The request failed'), life: 5000 });
    } finally {
      fixing.value = null;
    }
  }

  /** Titles with submitted text fixes their last parse hasn't seen; a reparse puts the fixes into their word lists and stats. */
  async function loadCorrected() {
    try {
      corrected.value = await $api<CorrectedTitle[]>('admin/sentence-flags/corrected-titles');
    } catch {
      corrected.value = [];
    }
  }

  async function reparseCorrected() {
    reparsing.value = true;
    try {
      const { queued } = await $api<{ queued: number }>('admin/sentence-flags/reparse-corrected', { method: 'POST' });
      toast.add({
        severity: 'success',
        summary: 'Reparse queued',
        detail: `${queued} title${queued === 1 ? '' : 's'}. Stats and word lists update when each finishes.`,
        life: 4000,
      });
      corrected.value = [];
    } catch (e) {
      toast.add({ severity: 'error', summary: "Couldn't queue the reparse", detail: extractApiError(e, 'The request failed'), life: 5000 });
    } finally {
      reparsing.value = false;
    }
  }

  watch(reason, () => {
    page.value = 0;
    load();
  });

  onMounted(() => {
    load();
    loadCorrected();
    // The submissions tab's queue reports its own count
    if (tab.value === 'flags')
      $api<{ pending: number }>('admin/sentence-submissions/count')
        .then((r) => (pendingSubmissions.value = r.pending))
        .catch(() => {});
  });
</script>

<template>
  <div class="container mx-auto p-4">
    <div class="flex items-center mb-2">
      <Button icon="pi pi-arrow-left" class="p-button-text mr-2" aria-label="Back to the dashboard" @click="navigateTo('/dashboard')" />
      <h1 class="text-3xl font-bold">Flagged Sentences</h1>
    </div>
    <Tabs v-model:value="tab" :show-navigators="false" class="mb-4">
      <TabList>
        <Tab value="flags">Flagged lines</Tab>
        <Tab value="submissions">
          Submitted fixes
          <Tag v-if="pendingSubmissions" :value="String(pendingSubmissions)" class="ml-1" />
        </Tab>
      </TabList>
    </Tabs>

    <SentenceSubmissionQueue v-if="tab === 'submissions'" @count="(n) => (pendingSubmissions = n)" />

    <template v-else>
      <p class="text-sm text-gray-600 dark:text-gray-300 mb-4">
        Lines users have flagged as wrong, hidden for each of them until it's fixed. Fix opens the fix screen, where Submit fixes the line for everyone and
        closes every flag on it. Mark fixed closes them after a fix made some other way, and Not a problem closes them when nothing was wrong. Either brings the
        line back for those users. Users' private lines aren't listed.
      </p>

      <div v-if="corrected.length" class="mb-4 p-3 rounded border border-surface-200 dark:border-surface-700 flex flex-col sm:flex-row sm:items-center gap-3">
        <div class="min-w-0 text-sm">
          <div class="font-semibold">Corrected titles to reparse</div>
          <div class="text-gray-600 dark:text-gray-300">
            Submitted fixes change word lists and stats only after a reparse:
            <template v-for="(t, i) in corrected" :key="t.deckId">{{ i ? ', ' : '' }}{{ t.title }} ({{ t.corrections }})</template>
          </div>
        </div>
        <Button
          :label="`Reparse ${corrected.length} title${corrected.length === 1 ? '' : 's'}`"
          icon="pi pi-refresh"
          size="small"
          class="shrink-0 sm:ml-auto"
          :loading="reparsing"
          @click="reparseCorrected"
        />
      </div>

      <div class="mb-4 flex flex-wrap items-center gap-4">
        <Tag severity="warn" class="text-base px-3 py-1">{{ totalCount }} flagged line{{ totalCount === 1 ? '' : 's' }}</Tag>
        <Select
          v-model="reason"
          :options="[...FLAG_REASONS]"
          option-label="label"
          option-value="value"
          placeholder="Any reason"
          show-clear
          class="w-56"
          aria-label="Reason"
        />
      </div>

      <div v-if="loading && items.length === 0" class="flex justify-center py-12">
        <ProgressSpinner style="width: 50px; height: 50px" stroke-width="4" />
      </div>

      <p v-else-if="items.length === 0" class="text-gray-500 py-8 text-center">No flagged lines.</p>

      <div v-else class="flex flex-col divide-y divide-surface-200 dark:divide-surface-700" :class="{ 'opacity-60': loading }">
        <div v-for="line in items" :key="line.sentenceId" class="py-3 flex flex-col sm:flex-row sm:items-start sm:justify-between gap-3">
          <div class="min-w-0">
            <NuxtLink v-if="line.hasPage" :to="`/sentences/${line.sentenceId}`" class="hover:underline text-lg" lang="ja">{{ line.text }}</NuxtLink>
            <span v-else class="text-lg" lang="ja">{{ line.text }}</span>

            <div class="flex flex-wrap items-center gap-2 mt-1 text-xs text-gray-500">
              <Tag :value="`${line.flagCount} flag${line.flagCount === 1 ? '' : 's'}`" severity="danger" />
              <Tag v-for="(count, r) in line.reasons" :key="r" :value="`${reasonLabel(r)} ×${count}`" severity="secondary" />
              <Tag v-if="line.removedFromDeck" value="No longer in the deck" severity="warn" />
              <NuxtLink v-if="line.deckId && !line.removedFromDeck" :to="`/decks/media/${line.deckId}/detail`" class="underline">
                {{ line.title }}<template v-if="line.episode"> – {{ line.episode }}</template>
              </NuxtLink>
              <span v-else-if="line.title"
                >{{ line.title }}<template v-if="line.episode"> – {{ line.episode }}</template></span
              >
              <span>since {{ new Date(line.firstFlaggedAt).toLocaleDateString() }}</span>
            </div>

            <ul v-if="line.notes.length" class="mt-2 flex flex-col gap-1">
              <li v-for="(n, i) in line.notes" :key="i" class="text-sm text-gray-600 dark:text-gray-300">
                <span class="text-xs text-gray-500">{{ n.reason ? reasonLabel(n.reason) : 'No reason yet' }}:</span>
                {{ n.note }}
              </li>
            </ul>
          </div>

          <div class="flex flex-wrap gap-2 shrink-0 self-start">
            <Button label="Fix" icon="pi pi-pencil" size="small" @click="navigateTo(`/sentences/${line.sentenceId}/fix`)" />
            <Button
              label="Mark fixed"
              icon="pi pi-check"
              size="small"
              severity="success"
              outlined
              :loading="fixing === line.sentenceId"
              @click="close(line, 'fixed')"
            />
            <Button
              label="Not a problem"
              icon="pi pi-times"
              size="small"
              severity="secondary"
              outlined
              :disabled="fixing === line.sentenceId"
              @click="close(line, 'reject')"
            />
          </div>
        </div>
      </div>

      <Paginator
        v-if="totalCount > rows"
        :first="page * rows"
        :rows="rows"
        :total-records="totalCount"
        class="mt-4"
        @page="
          (e) => {
            page = e.page;
            load();
          }
        "
      />
    </template>
  </div>
</template>
