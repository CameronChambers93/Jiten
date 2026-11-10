<script setup lang="ts">
  import Card from 'primevue/card';
  import Button from 'primevue/button';
  import Checkbox from 'primevue/checkbox';
  import InputNumber from 'primevue/inputnumber';
  import InputText from 'primevue/inputtext';
  import Message from 'primevue/message';
  import ProgressBar from 'primevue/progressbar';
  import Select from 'primevue/select';
  import Tag from 'primevue/tag';
  import { useToast } from 'primevue/usetoast';

  // Imports episodes of this title from local video: pick videos under the media roots, scan them for subtitles
  // (embedded, Jimaku, beside the video), then import in the background and show what each episode got.
  const props = defineProps<{ deckId: number }>();

  interface FolderEntry {
    name: string;
    path: string;
    isFolder: boolean;
    episode: number | null;
  }

  interface FolderListing {
    path: string | null;
    parent: string | null;
    entries: FolderEntry[];
    roots: string[];
  }

  interface Choice {
    id: string;
    label: string;
    detail: string | null;
  }

  interface ScanRow {
    videoPath: string;
    fileName: string;
    episode: number | null;
    existingDeckId: number | null;
    existingTitle: string | null;
    existingSentences: number;
    existingHasVideo: boolean;
    existingHasEnglish: boolean;
    mined: number;
    openFlags: number;
    conflict: string | null;
    japanese: Choice[];
    japaneseChoice: string | null;
    english: Choice[];
    englishChoice: string;
    audio: { index: number; label: string }[];
    audioStream: number | null;
  }

  interface ScanResult {
    parentId: number;
    title: string;
    jimakuEntryId: number | null;
    jimakuName: string | null;
    jimakuError: string | null;
    rows: ScanRow[];
  }

  // What the admin can change per row before importing
  interface RowEdit {
    include: boolean;
    episode: number | null;
    japanese: string | null;
    english: string;
    audio: number | null;
    offset: number | null;
  }

  interface Report {
    deckId: number;
    sentences: number;
    matched: number;
    withEnglish: number;
    unmatched: string[];
    japaneseSource: string;
    englishSource: string | null;
    videoOffsetMs: number;
    videoFit: string;
    audioOffsetMs: number | null;
    englishOffsetMs: number | null;
    segments: { fromMs: number; offsetMs: number }[];
    warnings: string[];
    keptMined: number;
    keptFlags: number;
  }

  interface EpisodeProgress {
    episode: number;
    fileName: string;
    step: number;
    error: string | null;
    report: Report | null;
  }

  interface Status {
    run: { id: string; parentId: number; startedAt: string; episodes: EpisodeProgress[]; finished: boolean; titleNote: string | null };
    iPlusOne: Record<string, number>;
  }

  // EpisodeImportStep on the API
  const steps = ['Queued', 'Subtitles', 'Timing', 'Parsing sentences', 'Video', 'English', 'Done', 'Failed'];
  const workSteps = [1, 2, 3, 4, 5];

  const { $api } = useNuxtApp();
  const toast = useToast();

  const folder = ref<FolderListing | null>(null);
  const pathInput = ref('');
  const picked = ref<string[]>([]);
  const jimakuId = ref<number | null>(null);
  const scanning = ref(false);
  const scan = ref<ScanResult | null>(null);
  const edits = ref<RowEdit[]>([]);
  const importing = ref(false);
  const status = ref<Status | null>(null);
  let pollTimer: ReturnType<typeof setTimeout> | null = null;

  const videos = computed(() => folder.value?.entries.filter((e) => !e.isFolder) ?? []);
  const toImport = computed(() =>
    (scan.value?.rows ?? []).map((r, i) => ({ row: r, edit: edits.value[i]! })).filter(({ edit }) => edit.include && edit.episode && edit.japanese && edit.audio != null)
  );
  const running = computed(() => status.value && !status.value.run.finished);

  async function browse(path: string | null) {
    try {
      folder.value = await $api<FolderListing>('admin/media-files', { query: path ? { path } : {} });
      pathInput.value = folder.value.path ?? '';
      picked.value = [];
    } catch (e) {
      toast.add({ severity: 'error', summary: 'Folder', detail: extractApiError(e, "Couldn't open that folder"), life: 5000 });
    }
  }

  function toggleAll() {
    picked.value = picked.value.length === videos.value.length ? [] : videos.value.map((v) => v.path);
  }

  async function runScan() {
    const paths = picked.value.length ? picked.value : folder.value?.path ? [folder.value.path] : [];
    if (!paths.length) return;
    scanning.value = true;
    try {
      const result = await $api<ScanResult>(`admin/decks/${props.deckId}/episode-import/scan`, {
        method: 'POST',
        body: { paths, jimakuEntryId: jimakuId.value },
      });
      scan.value = result;
      jimakuId.value = result.jimakuEntryId;
      edits.value = result.rows.map((r) => ({
        // Episodes holding mined lines or open flags need a deliberate tick
        include: !r.conflict && r.mined + r.openFlags === 0,
        episode: r.episode,
        japanese: r.japaneseChoice,
        english: r.englishChoice,
        audio: r.audioStream,
        offset: null,
      }));
    } catch (e) {
      toast.add({ severity: 'error', summary: 'Scan failed', detail: extractApiError(e, "Couldn't scan those videos"), life: 6000 });
    } finally {
      scanning.value = false;
    }
  }

  async function runImport() {
    importing.value = true;
    try {
      const { runId } = await $api<{ runId: string }>(`admin/decks/${props.deckId}/episode-import`, {
        method: 'POST',
        body: {
          items: toImport.value.map(({ row, edit }) => ({
            videoPath: row.videoPath,
            episode: edit.episode,
            japanese: edit.japanese,
            english: edit.english,
            audioStream: edit.audio,
            offsetMs: edit.offset,
            // The tick confirms replacing only the episode the scan showed; a changed number is checked again on the server
            confirmReplace: row.existingDeckId != null && edit.episode === row.episode,
          })),
        },
      });
      scan.value = null;
      await poll(runId);
    } catch (e) {
      toast.add({ severity: 'error', summary: 'Import failed', detail: extractApiError(e, "Couldn't start the import"), life: 6000 });
    } finally {
      importing.value = false;
    }
  }

  async function poll(runId: string) {
    if (pollTimer) clearTimeout(pollTimer);
    try {
      status.value = await $api<Status>(`admin/episode-import/${runId}`);
    } catch {
      status.value = null;
      return;
    }
    if (!status.value.run.finished) pollTimer = setTimeout(() => poll(runId), 3000);
  }

  async function loadLatest() {
    try {
      const latest = await $api<Status | null>(`admin/decks/${props.deckId}/episode-import/latest`);
      if (latest?.run) {
        status.value = latest;
        if (!latest.run.finished) await poll(latest.run.id);
      }
    } catch {
      // No earlier run
    }
  }

  onMounted(() => {
    browse(null);
    loadLatest();
  });
  onBeforeUnmount(() => pollTimer && clearTimeout(pollTimer));

  function rowState(r: ScanRow) {
    if (r.conflict) return { label: 'Conflict', severity: 'danger' };
    if (r.existingDeckId) return { label: 'Replaces', severity: 'warn' };
    return { label: 'New', severity: 'info' };
  }

  function ms(v: number | null | undefined) {
    if (v == null) return '–';
    return `${v > 0 ? '+' : v < 0 ? '−' : ''}${Math.abs(v).toLocaleString()} ms`;
  }

  function stepState(e: EpisodeProgress, step: number) {
    if (e.step === 7) return 'text-gray-400';
    if (e.step > step) return 'text-green-600';
    if (e.step === step) return 'font-semibold';
    return 'text-gray-400';
  }
</script>

<template>
  <Card class="mb-6">
    <template #title>Import episodes</template>
    <template #content>
      <div class="flex flex-col gap-4">
        <!-- Pick -->
        <div class="flex flex-wrap items-end gap-2">
          <div class="flex-1 min-w-64">
            <label for="episode-import-path" class="text-xs block mb-1">Video or season folder</label>
            <InputText id="episode-import-path" v-model="pathInput" class="w-full font-mono text-sm" @keyup.enter="browse(pathInput)" />
          </div>
          <div>
            <label for="episode-import-jimaku" class="text-xs block mb-1">Jimaku entry</label>
            <InputNumber id="episode-import-jimaku" v-model="jimakuId" :use-grouping="false" placeholder="found from links" class="w-40" input-class="w-40" />
          </div>
          <Button label="Scan" icon="pi pi-search" :loading="scanning" :disabled="!picked.length && !folder?.path" @click="runScan" />
        </div>

        <div v-if="folder" class="border border-surface-200 dark:border-surface-700 rounded text-sm max-h-80 overflow-y-auto">
          <div class="flex items-center gap-2 px-3 py-1.5 text-gray-500">
            <Button v-if="folder.path" icon="pi pi-arrow-up" text size="small" severity="secondary" aria-label="Up" @click="browse(folder.parent)" />
            <span class="font-mono truncate flex-1">{{ folder.path ?? 'Media roots' }}</span>
            <span v-if="videos.length">{{ videos.length }} videos</span>
            <Button v-if="videos.length" :label="picked.length === videos.length ? 'Select none' : 'Select all'" text size="small" @click="toggleAll" />
          </div>
          <div
            v-for="e in folder.entries"
            :key="e.path"
            class="flex items-center gap-2 px-3 py-1.5 border-t border-surface-200 dark:border-surface-700"
            :class="picked.includes(e.path) ? 'bg-purple-50 dark:bg-purple-950' : ''"
          >
            <template v-if="e.isFolder">
              <i class="pi pi-folder text-gray-500" />
              <a href="#" class="font-mono truncate" @click.prevent="browse(e.path)">{{ e.name }}</a>
            </template>
            <template v-else>
              <Checkbox v-model="picked" :value="e.path" :input-id="`ep-${e.path}`" />
              <label :for="`ep-${e.path}`" class="font-mono truncate flex-1 cursor-pointer">{{ e.name }}</label>
              <span v-if="e.episode" class="text-gray-500 tabular-nums">ep {{ e.episode }}</span>
            </template>
          </div>
          <div v-if="!folder.entries.length" class="px-3 py-2 border-t border-surface-200 dark:border-surface-700 text-gray-500">No folders or videos here.</div>
        </div>
        <p class="text-xs text-gray-500 -mt-2">
          Only folders under the media roots are listed. Tick videos, or scan a whole folder. The Jimaku entry is found from the title's AniList or TMDB
          link when left empty.
        </p>

        <!-- Scan result -->
        <div v-if="scan" class="flex flex-col gap-3">
          <h3 class="font-semibold">{{ scan.rows.length }} {{ scan.rows.length === 1 ? 'video' : 'videos' }} scanned</h3>
          <Message v-if="scan.jimakuError" severity="warn">{{ scan.jimakuError }}</Message>
          <p v-else-if="scan.jimakuName" class="text-sm text-gray-500">Jimaku: {{ scan.jimakuName }} (entry {{ scan.jimakuEntryId }})</p>
          <div class="overflow-x-auto">
            <table class="w-full text-sm">
              <thead>
                <tr class="text-left text-gray-500">
                  <th class="py-1 pr-2">Import</th>
                  <th class="py-1 pr-2">Ep</th>
                  <th class="py-1 pr-2">Episode</th>
                  <th class="py-1 pr-2">Japanese</th>
                  <th class="py-1 pr-2">English</th>
                  <th class="py-1 pr-2">Audio</th>
                  <th class="py-1 pr-2">Offset</th>
                </tr>
              </thead>
              <tbody>
                <tr v-for="(r, i) in scan.rows" :key="r.videoPath" class="border-t border-surface-200 dark:border-surface-700 align-top">
                  <td class="py-2 pr-2"><Checkbox v-model="edits[i]!.include" binary :disabled="!!r.conflict && r.episode != null" :aria-label="`Import ${r.fileName}`" /></td>
                  <td class="py-2 pr-2">
                    <InputNumber v-model="edits[i]!.episode" :min="1" :max="999" size="small" class="w-16" input-class="w-16" :aria-label="`Episode number for ${r.fileName}`" />
                  </td>
                  <td class="py-2 pr-2 min-w-48">
                    <Tag :value="rowState(r).label" :severity="rowState(r).severity" />
                    <div class="text-xs text-gray-500 mt-1 font-mono break-all">{{ r.fileName }}</div>
                    <div v-if="r.conflict" class="text-xs text-red-500 mt-1">{{ r.conflict }}</div>
                    <div v-else-if="r.existingDeckId" class="text-xs text-gray-500 mt-1">
                      {{ r.existingTitle }} (deck {{ r.existingDeckId }}): {{ r.existingSentences }} sentences,
                      {{ r.existingHasVideo ? 'video linked' : 'no video' }}{{ r.existingHasEnglish ? ', English' : '' }}
                      <span v-if="r.mined + r.openFlags" class="text-orange-500">
                        · {{ r.mined }} mined, {{ r.openFlags }} open flags; they're kept, tick Import to replace
                      </span>
                    </div>
                  </td>
                  <td class="py-2 pr-2 min-w-56">
                    <Select v-model="edits[i]!.japanese" :options="r.japanese" option-label="label" option-value="id" size="small" class="w-full max-w-80" placeholder="None found" />
                  </td>
                  <td class="py-2 pr-2 min-w-48">
                    <Select v-model="edits[i]!.english" :options="r.english" option-label="label" option-value="id" size="small" class="w-full max-w-64" />
                  </td>
                  <td class="py-2 pr-2">
                    <Select v-model="edits[i]!.audio" :options="r.audio" option-label="label" option-value="index" size="small" class="w-32" />
                  </td>
                  <td class="py-2 pr-2">
                    <InputNumber v-model="edits[i]!.offset" placeholder="auto" suffix=" ms" size="small" class="w-28" input-class="w-28" :aria-label="`Offset for ${r.fileName}`" />
                  </td>
                </tr>
              </tbody>
            </table>
          </div>
          <p class="text-xs text-gray-500">
            Replacing an episode keeps its mined and flagged lines. Leave the offset on auto to fit it from the audio (and the embedded English, when
            there is some); a typed offset is used as is.
          </p>
          <div class="flex flex-wrap items-center gap-3">
            <Button
              :label="`Import ${toImport.length} ${toImport.length === 1 ? 'episode' : 'episodes'}`"
              icon="pi pi-download"
              :loading="importing"
              :disabled="!toImport.length || !!running"
              @click="runImport"
            />
            <span class="text-xs text-gray-500">One at a time, in the background. You can leave the page.</span>
          </div>
        </div>

        <!-- Progress and report -->
        <div v-if="status" class="flex flex-col gap-3">
          <h3 class="font-semibold">{{ running ? 'Importing' : 'Last import' }}</h3>
          <div v-for="e in status.run.episodes" :key="e.episode" class="border border-surface-200 dark:border-surface-700 rounded p-3 flex flex-col gap-2">
            <div class="flex flex-wrap items-baseline justify-between gap-2">
              <span class="font-semibold">
                Episode {{ e.episode }}
                <span v-if="e.report" class="text-xs text-gray-500 font-normal">deck {{ e.report.deckId }}</span>
              </span>
              <Tag v-if="e.step === 6" :value="e.report && e.report.segments.length > 1 ? 'Done, retimed' : 'Done'" :severity="e.report && e.report.segments.length > 1 ? 'warn' : 'success'" />
              <Tag v-else-if="e.step === 7" value="Failed" severity="danger" />
              <Tag v-else-if="e.step === 0" value="Queued" severity="secondary" />
              <Tag v-else value="Importing" severity="info" />
            </div>

            <template v-if="e.step > 0 && e.step < 6">
              <ProgressBar :value="Math.round((e.step / 6) * 100)" :show-value="false" style="height: 6px" />
              <div class="flex flex-wrap gap-x-3 gap-y-1 text-sm">
                <span v-for="s in workSteps" :key="s" :class="stepState(e, s)">{{ e.step > s ? '✓ ' : '' }}{{ steps[s] }}</span>
              </div>
            </template>

            <Message v-if="e.error" severity="error">{{ e.error }}</Message>

            <template v-if="e.report">
              <Message v-for="(w, wi) in e.report.warnings" :key="wi" severity="warn" class="text-sm">{{ w }}</Message>
              <div class="grid grid-cols-[repeat(auto-fit,minmax(150px,1fr))] gap-x-4 gap-y-2 text-sm">
                <div>
                  <div class="text-lg tabular-nums">{{ e.report.matched }} / {{ e.report.sentences }}</div>
                  sentences with a clip
                </div>
                <div>
                  <div class="text-lg tabular-nums">{{ e.report.withEnglish }}</div>
                  with English
                </div>
                <div v-if="status.iPlusOne[e.report.deckId] != null">
                  <div class="text-lg tabular-nums">{{ status.iPlusOne[e.report.deckId] }}</div>
                  i+1 for you
                </div>
                <div>
                  <div class="text-lg tabular-nums">{{ ms(e.report.videoOffsetMs) }}</div>
                  video offset · {{ e.report.videoFit }}<span v-if="e.report.audioOffsetMs != null"> (audio {{ ms(e.report.audioOffsetMs) }})</span>
                </div>
                <div v-if="e.report.englishOffsetMs != null">
                  <div class="text-lg tabular-nums">{{ ms(e.report.englishOffsetMs) }}</div>
                  English offset
                </div>
              </div>
              <p class="text-xs text-gray-500">
                Japanese: {{ e.report.japaneseSource }}<template v-if="e.report.englishSource"> · English: {{ e.report.englishSource }}</template>
                <template v-if="e.report.keptMined + e.report.keptFlags"> · kept {{ e.report.keptMined }} mined lines and {{ e.report.keptFlags }} open flags</template>
              </p>
              <p v-if="e.report.unmatched.length" class="text-xs text-gray-500">
                No clip: <span lang="ja">{{ e.report.unmatched.map((u) => `「${u}」`).join(' ') }}</span>
              </p>
              <p class="text-sm">
                <NuxtLink :to="`/decks/media/${e.report.deckId}/sentences`" class="text-primary-500 hover:underline">Sentences</NuxtLink>
              </p>
            </template>
          </div>
          <p v-if="status.run.titleNote" class="text-xs text-gray-500">{{ status.run.titleNote }}</p>
        </div>
      </div>
    </template>
  </Card>
</template>
