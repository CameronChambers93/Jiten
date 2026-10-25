<script setup lang="ts">
  import Card from 'primevue/card';
  import Button from 'primevue/button';
  import Checkbox from 'primevue/checkbox';
  import InputNumber from 'primevue/inputnumber';
  import InputText from 'primevue/inputtext';
  import Message from 'primevue/message';
  import Select from 'primevue/select';
  import { useToast } from 'primevue/usetoast';
  import { useConfirm } from 'primevue/useconfirm';

  // English for a subtitle title's episodes: look up English subtitles, preview how they line up, save them; then translate
  // the sentences still without English using the AI provider.
  const props = defineProps<{ deckId: number }>();

  interface Episode {
    deckId: number;
    episode: number;
    title: string;
    hasJapaneseCues: boolean;
    englishSource: string | null;
    englishOffsetMs: number | null;
    englishCueCount: number | null;
  }

  interface TitleInfo {
    query: string;
    season: number | null;
    sources: { id: string; name: string; configured: boolean }[];
    episodes: Episode[];
  }

  interface Candidate {
    id: string;
    episode: number;
    release: string;
    downloads: number | null;
    note: string | null;
  }

  interface Preview {
    deckId: number;
    token: string | null;
    release: string;
    cueCount: number;
    offsetMs: number;
    coverage: number;
    coverageUnshifted: number;
    samples: { japanese: string; english: string | null }[];
    error: string | null;
  }

  interface Estimate {
    sentences: number;
    fromSubtitles: number;
    cachedAi: number;
    needAi: number;
    provider: string | null;
    model: string | null;
    estimatedCostUsd: number | null;
    progress: { total: number; done: number; failed: number; lastError: string | null; running: boolean } | null;
  }

  const { $api } = useNuxtApp();
  const toast = useToast();
  const confirm = useConfirm();

  const info = ref<TitleInfo | null>(null);
  const sourceId = ref('');
  const query = ref('');
  const season = ref<number | null>(null);
  const searching = ref(false);
  const candidates = ref<Candidate[] | null>(null);
  // Per episode deck id: the chosen candidate id, or '' to skip the episode.
  const picks = ref<Record<number, string>>({});
  const previewing = ref(false);
  const previews = ref<Preview[]>([]);
  const offsets = ref<Record<number, number>>({});
  const keep = ref<Record<number, boolean>>({});
  const expanded = ref<number | null>(null);
  const saving = ref(false);
  const estimate = ref<Estimate | null>(null);
  let pollTimer: ReturnType<typeof setTimeout> | null = null;

  const linkedEpisodes = computed(() => info.value?.episodes.filter((e) => e.hasJapaneseCues) ?? []);
  const episodeByDeck = computed(() => Object.fromEntries((info.value?.episodes ?? []).map((e) => [e.deckId, e])));

  async function load() {
    try {
      const data = await $api<TitleInfo>(`admin/translation/titles/${props.deckId}/english`);
      info.value = data;
      query.value ||= data.query;
      season.value ??= data.season;
      sourceId.value ||= data.sources.find((s) => s.configured && s.id !== 'embedded')?.id ?? 'embedded';
      if (data.episodes.some((e) => e.hasJapaneseCues)) await loadEstimate();
    } catch {
      info.value = null;
    }
  }

  async function loadEstimate() {
    try {
      estimate.value = await $api<Estimate>(`admin/translation/titles/${props.deckId}/estimate`);
    } catch {
      estimate.value = null;
    }
    if (pollTimer) clearTimeout(pollTimer);
    if (estimate.value?.progress?.running) pollTimer = setTimeout(loadEstimate, 3000);
  }

  onMounted(load);
  onBeforeUnmount(() => pollTimer && clearTimeout(pollTimer));

  function candidatesFor(episode: number) {
    return (candidates.value ?? []).filter((c) => c.episode === episode);
  }

  function candidateLabel(c: Candidate) {
    return [c.release, c.note, c.downloads != null ? `${c.downloads} downloads` : null].filter(Boolean).join(' · ');
  }

  async function search() {
    searching.value = true;
    previews.value = [];
    try {
      const { candidates: found } = await $api<{ candidates: Candidate[] }>(`admin/translation/titles/${props.deckId}/english/search`, {
        method: 'POST',
        body: { sourceId: sourceId.value, query: query.value, season: season.value },
      });
      candidates.value = found;
      picks.value = Object.fromEntries(linkedEpisodes.value.map((e) => [e.deckId, candidatesFor(e.episode)[0]?.id ?? '']));
    } catch (e) {
      toast.add({ severity: 'error', summary: 'Search failed', detail: extractApiError(e, 'The source returned an error'), life: 6000 });
    } finally {
      searching.value = false;
    }
  }

  async function preview() {
    const items = linkedEpisodes.value
      .filter((e) => picks.value[e.deckId])
      .map((e) => {
        const candidate = candidates.value!.find((c) => c.id === picks.value[e.deckId])!;
        return { deckId: e.deckId, sourceId: sourceId.value, candidateId: candidate.id, release: candidate.release };
      });
    if (items.length === 0) return;
    previewing.value = true;
    try {
      const { previews: result } = await $api<{ previews: Preview[] }>(`admin/translation/titles/${props.deckId}/english/preview`, {
        method: 'POST',
        body: { items },
      });
      previews.value = result;
      offsets.value = Object.fromEntries(result.map((p) => [p.deckId, p.offsetMs]));
      keep.value = Object.fromEntries(result.map((p) => [p.deckId, !!p.token]));
    } catch (e) {
      toast.add({ severity: 'error', summary: 'Preview failed', detail: extractApiError(e, 'Failed to download or align'), life: 6000 });
    } finally {
      previewing.value = false;
    }
  }

  async function save() {
    const items = previews.value
      .filter((p) => p.token && keep.value[p.deckId])
      .map((p) => ({ deckId: p.deckId, token: p.token, offsetMs: offsets.value[p.deckId] }));
    saving.value = true;
    try {
      const { saved } = await $api<{ saved: number }>(`admin/translation/titles/${props.deckId}/english/save`, { method: 'POST', body: { items } });
      toast.add({ severity: 'success', summary: `Saved English for ${saved} episodes`, life: 3000 });
      if (saved < items.length) toast.add({ severity: 'warn', summary: 'Some previews expired; preview them again', life: 5000 });
      previews.value = [];
      candidates.value = null;
      await load();
    } catch (e) {
      toast.add({ severity: 'error', summary: 'Error', detail: extractApiError(e, 'Failed to save'), life: 5000 });
    } finally {
      saving.value = false;
    }
  }

  function removeEnglish(episode: Episode) {
    confirm.require({
      message: `Remove the English subtitles of episode ${episode.episode}?`,
      header: 'Remove English',
      acceptProps: { label: 'Remove', severity: 'danger' },
      rejectProps: { label: 'Cancel', severity: 'secondary', outlined: true },
      accept: async () => {
        await $api(`admin/translation/decks/${episode.deckId}/english`, { method: 'DELETE' });
        await load();
      },
    });
  }

  function translateAll() {
    const e = estimate.value!;
    const cost = e.estimatedCostUsd != null ? ` (about $${e.estimatedCostUsd.toFixed(2)})` : '';
    confirm.require({
      message: `Translate ${e.needAi} sentences with ${e.provider} (${e.model})${cost}? It runs in the background.`,
      header: 'Translate sentences',
      acceptProps: { label: 'Translate' },
      rejectProps: { label: 'Cancel', severity: 'secondary', outlined: true },
      accept: async () => {
        try {
          await $api(`admin/translation/titles/${props.deckId}/translate`, { method: 'POST' });
          await loadEstimate();
        } catch (err) {
          toast.add({ severity: 'error', summary: 'Error', detail: extractApiError(err, 'Failed to start'), life: 5000 });
        }
      },
    });
  }

  function clearTitleCache() {
    confirm.require({
      message: "Delete this title's cached AI translations?",
      header: 'Clear AI translations',
      acceptProps: { label: 'Delete', severity: 'danger' },
      rejectProps: { label: 'Cancel', severity: 'secondary', outlined: true },
      accept: async () => {
        const { deleted } = await $api<{ deleted: number }>('admin/translation/cache', { method: 'DELETE', query: { deckId: props.deckId } });
        toast.add({ severity: 'success', summary: `Deleted ${deleted} translations`, life: 3000 });
        await loadEstimate();
      },
    });
  }

  const percent = (n: number) => `${Math.round(n * 100)}%`;
  const seconds = (ms: number | null) => (ms == null ? '' : `${ms >= 0 ? '+' : ''}${(ms / 1000).toFixed(2)} s`);
</script>

<template>
  <Card v-if="info && linkedEpisodes.length" class="mb-6">
    <template #title>English</template>
    <template #content>
      <div class="flex flex-col gap-5">
        <div>
          <h3 class="font-semibold mb-2">English subtitles per episode</h3>
          <div class="overflow-x-auto">
            <table class="w-full text-sm">
              <thead>
                <tr class="text-left text-gray-500">
                  <th class="py-1 pr-2">Ep</th>
                  <th class="py-1 pr-2">English</th>
                  <th class="py-1 pr-2">Offset</th>
                  <th />
                </tr>
              </thead>
              <tbody>
                <tr v-for="e in linkedEpisodes" :key="e.deckId" class="border-t border-surface-200 dark:border-surface-700">
                  <td class="py-1 pr-2 tabular-nums">{{ e.episode }}</td>
                  <td class="py-1 pr-2">
                    <span v-if="e.englishSource"
                      >{{ e.englishSource }} <span class="text-gray-500">({{ e.englishCueCount }} lines)</span></span
                    >
                    <span v-else class="text-gray-400">none</span>
                  </td>
                  <td class="py-1 pr-2 tabular-nums">{{ seconds(e.englishOffsetMs) }}</td>
                  <td class="py-1 text-right">
                    <Button v-if="e.englishSource" icon="pi pi-times" size="small" text severity="secondary" aria-label="Remove" @click="removeEnglish(e)" />
                  </td>
                </tr>
              </tbody>
            </table>
          </div>
        </div>

        <div>
          <h3 class="font-semibold mb-2">Find English subtitles</h3>
          <div class="flex flex-wrap items-end gap-2">
            <div>
              <label class="text-xs block mb-1">Source</label>
              <Select
                v-model="sourceId"
                :options="info.sources"
                option-label="name"
                option-value="id"
                :option-disabled="(s: TitleInfo['sources'][number]) => !s.configured"
                class="w-56"
              />
            </div>
            <div v-if="sourceId !== 'embedded'" class="flex-1 min-w-48">
              <label class="text-xs block mb-1">Show name</label>
              <InputText v-model="query" class="w-full" />
            </div>
            <div v-if="sourceId !== 'embedded'">
              <label class="text-xs block mb-1">Season</label>
              <InputNumber v-model="season" :min="0" :max="99" class="w-24" input-class="w-24" />
            </div>
            <Button label="Search" icon="pi pi-search" :loading="searching" @click="search" />
          </div>
          <p class="text-xs text-gray-500 mt-1">Sources without a key are greyed out; add keys on the Translation settings page.</p>

          <template v-if="candidates">
            <Message v-if="candidates.length === 0" severity="warn" class="mt-3">Nothing found. Try another name, season or source.</Message>
            <template v-else>
              <div class="mt-3 flex flex-col gap-1">
                <div v-for="e in linkedEpisodes" :key="e.deckId" class="flex items-center gap-2 text-sm">
                  <span class="w-10 tabular-nums">Ep {{ e.episode }}</span>
                  <Select
                    v-model="picks[e.deckId]"
                    :options="[{ id: '', label: 'Skip' }, ...candidatesFor(e.episode).map((c) => ({ id: c.id, label: candidateLabel(c) }))]"
                    option-label="label"
                    option-value="id"
                    class="flex-1 min-w-0"
                    size="small"
                  />
                </div>
              </div>
              <Button label="Preview" icon="pi pi-eye" class="mt-3" :loading="previewing" @click="preview" />
              <p v-if="previewing" class="text-xs text-gray-500 mt-1">Downloading and lining up each episode; this takes a moment per episode.</p>
            </template>
          </template>

          <div v-if="previews.length" class="mt-4 flex flex-col gap-2">
            <p class="text-xs text-gray-500">
              Fit is the share of the English lines' time that falls on Japanese lines after shifting by the offset; above about 80% means they line up.
            </p>
            <div v-for="p in previews" :key="p.deckId" class="border border-surface-200 dark:border-surface-700 rounded p-2 text-sm">
              <div class="flex flex-wrap items-center gap-3">
                <Checkbox v-model="keep[p.deckId]" binary :disabled="!p.token" />
                <span class="font-semibold w-12">Ep {{ episodeByDeck[p.deckId]?.episode }}</span>
                <span class="flex-1 min-w-0 truncate" :title="p.release">{{ p.release }}</span>
                <template v-if="p.token">
                  <span :class="p.coverage >= 0.8 ? 'text-green-600' : 'text-orange-500'" class="tabular-nums">fit {{ percent(p.coverage) }}</span>
                  <span class="text-gray-500 tabular-nums">(unshifted {{ percent(p.coverageUnshifted) }})</span>
                  <span class="flex items-center gap-1">
                    offset
                    <InputNumber v-model="offsets[p.deckId]" :step="100" suffix=" ms" size="small" class="w-32" input-class="w-28" />
                  </span>
                  <Button
                    :icon="expanded === p.deckId ? 'pi pi-chevron-up' : 'pi pi-chevron-down'"
                    size="small"
                    text
                    severity="secondary"
                    aria-label="Sample lines"
                    @click="expanded = expanded === p.deckId ? null : p.deckId"
                  />
                </template>
                <span v-else class="text-red-500">{{ p.error }}</span>
              </div>
              <ul v-if="expanded === p.deckId" class="mt-2 flex flex-col gap-1 pl-8">
                <li v-for="(s, i) in p.samples" :key="i">
                  <span lang="ja">{{ s.japanese }}</span>
                  <span class="text-gray-500"> → {{ s.english ?? '(no English line here)' }}</span>
                </li>
              </ul>
            </div>
            <div>
              <Button label="Save English" icon="pi pi-check" :loading="saving" :disabled="!previews.some((p) => p.token && keep[p.deckId])" @click="save" />
              <span class="text-xs text-gray-500 ml-2">The samples use the detected offset; an edited offset applies on save.</span>
            </div>
          </div>
        </div>

        <div v-if="estimate">
          <h3 class="font-semibold mb-2">Sentences</h3>
          <p class="text-sm">
            {{ estimate.sentences.toLocaleString() }} sentences: {{ estimate.fromSubtitles.toLocaleString() }} from English subtitles,
            {{ estimate.cachedAi.toLocaleString() }} translated by AI, {{ estimate.needAi.toLocaleString() }} without English.
          </p>
          <div v-if="estimate.progress" class="text-sm mt-1">
            <template v-if="estimate.progress.running">
              <i class="pi pi-spin pi-spinner text-xs" /> Translating: {{ estimate.progress.done }} of {{ estimate.progress.total }} done
            </template>
            <template v-else>Last run: {{ estimate.progress.done }} of {{ estimate.progress.total }} translated</template>
            <span v-if="estimate.progress.failed" class="text-orange-500">, {{ estimate.progress.failed }} failed ({{ estimate.progress.lastError }})</span>
          </div>
          <div class="flex flex-wrap items-center gap-2 mt-2">
            <Button
              v-if="estimate.needAi > 0"
              :label="`Translate ${estimate.needAi.toLocaleString()} with AI`"
              icon="pi pi-language"
              size="small"
              :disabled="!estimate.provider || !!estimate.progress?.running"
              @click="translateAll"
            />
            <span v-if="estimate.needAi > 0 && estimate.provider" class="text-xs text-gray-500">
              {{ estimate.provider }}, {{ estimate.model }};
              {{ estimate.estimatedCostUsd != null ? `about $${estimate.estimatedCostUsd.toFixed(2)}` : 'price unknown for this model' }}
            </span>
            <span v-else-if="estimate.needAi > 0" class="text-xs text-gray-500">Set up an AI provider on the Translation settings page.</span>
            <Button
              v-if="estimate.cachedAi > 0"
              label="Clear AI translations"
              icon="pi pi-trash"
              size="small"
              severity="danger"
              outlined
              @click="clearTitleCache"
            />
          </div>
        </div>
      </div>
    </template>
  </Card>
</template>
