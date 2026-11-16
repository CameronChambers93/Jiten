<script setup lang="ts">
  import Button from 'primevue/button';
  import Card from 'primevue/card';
  import Column from 'primevue/column';
  import DataTable from 'primevue/datatable';
  import InputText from 'primevue/inputtext';
  import Message from 'primevue/message';
  import { useToast } from 'primevue/usetoast';

  interface JobProgress {
    total: number;
    done: number;
    failed: number;
    lastError: string | null;
    running: boolean;
    queued: boolean;
    quotaReached: boolean;
  }

  interface TitleRow {
    deckId: number;
    title: string;
    englishTitle: string | null;
    sentences: number;
    progress: JobProgress | null;
  }

  interface CheckProgress {
    deckId: number;
    lines: number;
    passed: number;
    flagged: number;
    accepted: number;
    dismissed: number;
    leased: number;
  }

  interface Estimate {
    sentences: number;
    fromSubtitles: number;
    cachedAi: number;
    needAi: number;
    needAiCalls: number;
    needAiChars: number;
    provider: string | null;
  }

  /** Translate several titles in one run (subtitle English first, then the active provider), and see each title's checks. */
  const { $api } = useNuxtApp();
  const toast = useToast();

  const titles = ref<TitleRow[]>([]);
  const checks = ref<Record<number, CheckProgress>>({});
  const selected = ref<TitleRow[]>([]);
  const filter = ref('');
  const loading = ref(false);
  const starting = ref(false);
  const estimating = ref(false);
  const estimates = ref<Record<number, Estimate>>({});
  let poll: ReturnType<typeof setInterval> | null = null;

  const shown = computed(() => {
    const q = filter.value.trim().toLowerCase();
    return q ? titles.value.filter((t) => t.title.toLowerCase().includes(q) || t.englishTitle?.toLowerCase().includes(q)) : titles.value;
  });

  const selectedEstimate = computed(() => {
    const picked = selected.value.map((t) => estimates.value[t.deckId]).filter((e): e is Estimate => !!e);
    if (picked.length !== selected.value.length || picked.length === 0) return null;
    return {
      sentences: picked.reduce((n, e) => n + e.sentences, 0),
      fromSubtitles: picked.reduce((n, e) => n + e.fromSubtitles, 0),
      cachedAi: picked.reduce((n, e) => n + e.cachedAi, 0),
      needAi: picked.reduce((n, e) => n + e.needAi, 0),
      needAiCalls: picked.reduce((n, e) => n + e.needAiCalls, 0),
      needAiChars: picked.reduce((n, e) => n + e.needAiChars, 0),
      provider: picked[0]!.provider,
    };
  });

  const anyRunning = computed(() => titles.value.some((t) => t.progress?.running));

  /** Check counts change only while a Claude Code session runs, so they load once and on Refresh, not on every poll. */
  async function loadChecks() {
    try {
      const c = await $api<CheckProgress[]>('admin/sentence-checks/progress');
      checks.value = Object.fromEntries(c.map((p) => [p.deckId, p]));
    } catch (e) {
      toast.add({ severity: 'error', summary: "Couldn't load the checks", detail: extractApiError(e, 'The request failed'), life: 5000 });
    }
  }

  async function load() {
    loading.value = titles.value.length === 0;
    try {
      const t = await $api<TitleRow[]>('admin/translation/titles');
      titles.value = t;
      // Keep the selection pointing at the fresh rows
      const ids = new Set(selected.value.map((s) => s.deckId));
      selected.value = t.filter((row) => ids.has(row.deckId));
    } catch (e) {
      toast.add({ severity: 'error', summary: "Couldn't load the titles", detail: extractApiError(e, 'The request failed'), life: 5000 });
    } finally {
      loading.value = false;
    }
    setPolling();
  }

  function setPolling() {
    if (anyRunning.value && !poll) poll = setInterval(load, 5000);
    else if (!anyRunning.value && poll) {
      clearInterval(poll);
      poll = null;
    }
  }

  async function estimate() {
    estimating.value = true;
    try {
      for (const row of selected.value)
        if (!estimates.value[row.deckId]) estimates.value[row.deckId] = await $api<Estimate>(`admin/translation/titles/${row.deckId}/estimate`);
    } catch (e) {
      toast.add({ severity: 'error', summary: "Couldn't estimate", detail: extractApiError(e, 'The request failed'), life: 5000 });
    } finally {
      estimating.value = false;
    }
  }

  async function translate() {
    starting.value = true;
    try {
      await $api('admin/translation/titles/translate', { method: 'POST', body: { deckIds: selected.value.map((t) => t.deckId) } });
      toast.add({ severity: 'success', summary: 'Translation started', detail: `${selected.value.length} titles, one after another.`, life: 3000 });
      estimates.value = {};
      await load();
    } catch (e) {
      toast.add({ severity: 'error', summary: "Couldn't start", detail: extractApiError(e, 'The request failed'), life: 5000 });
    } finally {
      starting.value = false;
    }
  }

  function status(p: JobProgress | null) {
    if (!p) return '';
    if (p.queued) return 'Waiting';
    if (p.running) return `Translating ${p.done} / ${p.total}`;
    if (p.quotaReached) return p.total === 0 ? 'Not started: quota used up' : `Quota reached: ${p.total - p.done} left`;
    if (p.lastError && p.done < p.total) return `Stopped: ${p.done} / ${p.total} (${p.lastError})`;
    return `Done: ${p.done} translated${p.failed ? `, ${p.failed} failed` : ''}`;
  }

  function checked(c: CheckProgress | undefined) {
    return c ? c.passed + c.accepted + c.dismissed + c.flagged : 0;
  }

  onMounted(() => {
    load();
    loadChecks();
  });
  onBeforeUnmount(() => poll && clearInterval(poll));
</script>

<template>
  <Card class="shadow-md">
    <template #title>Bulk</template>
    <template #content>
      <p class="text-sm text-gray-600 dark:text-gray-300 mb-3">
        Pick titles to translate in one run: subtitle English where there is a track, the active provider for the rest. A run stops when the provider's quota
        is used up; start it again later and it carries on. Checks are made by Claude Code with <code>/check-sentences</code>; flagged lines wait on the
        Checks tab.
      </p>

      <div class="flex flex-col sm:flex-row gap-2 mb-3">
        <InputText v-model="filter" placeholder="Filter titles" class="sm:w-64" />
        <div class="flex gap-2 sm:ml-auto">
          <Button label="Refresh checks" icon="pi pi-refresh" text size="small" @click="loadChecks" />
          <Button label="Estimate" icon="pi pi-calculator" outlined size="small" :disabled="!selected.length" :loading="estimating" @click="estimate" />
          <Button
            :label="`Translate ${selected.length || ''} selected`"
            icon="pi pi-language"
            size="small"
            :disabled="!selected.length || selected.some((t) => t.progress?.running)"
            :loading="starting"
            @click="translate"
          />
        </div>
      </div>

      <Message v-if="selectedEstimate" severity="info" class="mb-3">
        {{ selectedEstimate.sentences }} lines: {{ selectedEstimate.fromSubtitles }} from subtitles, {{ selectedEstimate.cachedAi }} already translated,
        {{ selectedEstimate.needAi }} to translate: {{ selectedEstimate.needAiCalls }} distinct texts,
        {{ selectedEstimate.needAiChars.toLocaleString() }} characters, with
        {{ selectedEstimate.provider ?? 'no provider' }}.
      </Message>

      <DataTable
        v-model:selection="selected"
        :value="shown"
        data-key="deckId"
        :loading="loading"
        size="small"
        paginator
        :rows="20"
        scrollable
        class="text-sm"
      >
        <Column selection-mode="multiple" header-style="width: 3rem" />
        <Column field="title" header="Title">
          <template #body="{ data }">
            <span lang="ja">{{ data.title }}</span>
            <div v-if="data.englishTitle" class="text-xs text-gray-500">{{ data.englishTitle }}</div>
          </template>
        </Column>
        <Column field="sentences" header="Lines" />
        <Column header="Translation">
          <template #body="{ data }">
            <span :class="{ 'text-orange-600': data.progress?.quotaReached }">{{ status(data.progress) }}</span>
          </template>
        </Column>
        <Column header="Checked">
          <template #body="{ data }">
            <template v-if="checks[data.deckId]">
              {{ checked(checks[data.deckId]) }} / {{ data.sentences }}
              <NuxtLink
                v-if="checks[data.deckId]!.flagged"
                :to="`/dashboard/sentence-flags?tab=checks&deckId=${data.deckId}`"
                class="underline text-orange-600 ml-1"
              >
                {{ checks[data.deckId]!.flagged }} flagged
              </NuxtLink>
            </template>
            <span v-else class="text-gray-500">–</span>
          </template>
        </Column>
      </DataTable>
    </template>
  </Card>
</template>
