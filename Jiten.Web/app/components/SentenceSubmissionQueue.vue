<script setup lang="ts">
  import Button from 'primevue/button';
  import Dialog from 'primevue/dialog';
  import InputText from 'primevue/inputtext';
  import Paginator from 'primevue/paginator';
  import ProgressSpinner from 'primevue/progressspinner';
  import Tag from 'primevue/tag';
  import { useToast } from 'primevue/usetoast';
  import { textDiff } from '~/utils/textDiff';

  /** One version of a line's parts. Times are in subtitle time, padding included. */
  interface FixParts {
    text: string | null;
    english: string | null;
    startMs: number | null;
    endMs: number | null;
    pictureMs: number | null;
  }

  interface Submission {
    submissionId: number;
    sentenceId: number;
    title: string | null;
    episode: string | null;
    deckId: number | null;
    hasPage: boolean;
    hasVideo: boolean;
    removedFromDeck: boolean;
    submitter: string;
    createdAt: string;
    /** The shared line as everyone sees it now. */
    now: FixParts;
    /** Only the parts the submission changes. */
    proposed: FixParts;
    /** The line got another fix after this was submitted. */
    changed: boolean;
    sameText: number;
  }

  const emit = defineEmits<{ count: [pending: number] }>();

  const { $api } = useNuxtApp();
  const toast = useToast();

  const items = ref<Submission[]>([]);
  const totalCount = ref(0);
  const loading = ref(false);
  const page = ref(0);
  const rows = 50;
  const busy = ref<number | null>(null);

  async function load() {
    loading.value = true;
    try {
      const result = await $api<{ items: Submission[]; totalCount: number }>('admin/sentence-submissions', {
        query: { offset: page.value * rows, limit: rows },
      });
      items.value = result.items;
      totalCount.value = result.totalCount;
      emit('count', result.totalCount);
    } catch (e) {
      toast.add({ severity: 'error', summary: "Couldn't load the submitted fixes", detail: extractApiError(e, 'The request failed'), life: 5000 });
    } finally {
      loading.value = false;
    }
  }

  async function reviewed(summary: string) {
    toast.add({ severity: 'success', summary, life: 3000 });
    // The last entry on a page leaves it empty: step back.
    if (items.value.length === 1 && page.value > 0) page.value--;
    await load();
  }

  async function approve(item: Submission) {
    busy.value = item.submissionId;
    try {
      await $api(`admin/sentence-submissions/${item.submissionId}/approve`, { method: 'POST' });
      await reviewed('Approved: the line is fixed for everyone');
    } catch (e) {
      toast.add({ severity: 'error', summary: "Couldn't approve it", detail: extractApiError(e, 'The request failed'), life: 6000 });
    } finally {
      busy.value = null;
    }
  }

  const rejecting = ref<Submission | null>(null);
  const reason = ref('');
  function askReject(item: Submission) {
    rejecting.value = item;
    reason.value = '';
  }
  async function reject() {
    const item = rejecting.value;
    if (!item) return;
    busy.value = item.submissionId;
    try {
      await $api(`admin/sentence-submissions/${item.submissionId}/reject`, { method: 'POST', body: { note: reason.value.trim() || null } });
      rejecting.value = null;
      await reviewed('Rejected; the submitter is told');
    } catch (e) {
      toast.add({ severity: 'error', summary: "Couldn't reject it", detail: extractApiError(e, 'The request failed'), life: 6000 });
    } finally {
      busy.value = null;
    }
  }

  // ---- Previews: the clip and frame as they are, and as proposed ----
  let player: HTMLAudioElement | null = null;
  const playing = ref<string | null>(null);
  async function play(item: Submission, parts: FixParts, key: string) {
    if (parts.startMs == null || parts.endMs == null) return;
    player?.pause();
    playing.value = key;
    try {
      const blob = await $api<Blob>(`sentence-media/${item.sentenceId}/audio`, {
        responseType: 'blob',
        query: { startMs: parts.startMs, endMs: parts.endMs },
      });
      const url = URL.createObjectURL(blob);
      player = new Audio(url);
      player.addEventListener('ended', () => URL.revokeObjectURL(url));
      await player.play();
    } catch (e) {
      toast.add({ severity: 'error', summary: "Couldn't cut the clip", detail: extractApiError(e, ''), life: 5000 });
    } finally {
      playing.value = null;
    }
  }

  const frames = ref<Record<string, string>>({});
  async function loadFrame(item: Submission, atMs: number | null, key: string) {
    if (atMs == null || frames.value[key]) return;
    try {
      const blob = await $api<Blob>(`sentence-media/${item.sentenceId}/picture`, { responseType: 'blob', query: { atMs } });
      frames.value = { ...frames.value, [key]: URL.createObjectURL(blob) };
    } catch {
      // No frame: the time shows instead
    }
  }

  /** The frame a proposal shows: its own, else the middle of its new clip. */
  function proposedFrame(item: Submission) {
    const p = item.proposed;
    return p.pictureMs ?? (p.startMs != null && p.endMs != null ? Math.round((p.startMs + p.endMs) / 2) : null);
  }
  const showsFrame = (item: Submission) => item.hasVideo && (item.proposed.pictureMs != null || item.proposed.startMs != null);

  watch(items, (list) => {
    for (const item of list) {
      if (!showsFrame(item)) continue;
      loadFrame(item, item.now.pictureMs, `${item.submissionId}-now`);
      loadFrame(item, proposedFrame(item), `${item.submissionId}-new`);
    }
  });

  function changedParts(item: Submission) {
    const p = item.proposed;
    return [p.text != null && 'Japanese', p.english != null && 'English', p.startMs != null && 'Clip', p.pictureMs != null && 'Frame'].filter(
      Boolean
    ) as string[];
  }

  function formatTime(ms: number | null) {
    if (ms == null) return '–';
    const seconds = ms / 1000;
    return `${Math.floor(seconds / 60)}:${(seconds % 60).toFixed(1).padStart(4, '0')}`;
  }

  onMounted(load);
  onBeforeUnmount(() => {
    player?.pause();
    for (const url of Object.values(frames.value)) URL.revokeObjectURL(url);
  });
  defineExpose({ load });
</script>

<template>
  <div class="flex flex-col gap-3">
    <p class="text-sm text-gray-600 dark:text-gray-300">
      Fixes users submitted, oldest first. Approve makes a fix the shared line for everyone, as your own Submit does; Reject sends the submitter your reason.
      Either way their own fix stays theirs.
    </p>

    <div v-if="loading && items.length === 0" class="flex justify-center py-12">
      <ProgressSpinner style="width: 50px; height: 50px" stroke-width="4" />
    </div>

    <p v-else-if="items.length === 0" class="text-gray-500 py-8 text-center">No submitted fixes.</p>

    <div v-else class="flex flex-col divide-y divide-surface-200 dark:divide-surface-700" :class="{ 'opacity-60': loading }">
      <div v-for="item in items" :key="item.submissionId" class="py-3 flex flex-col gap-3">
        <div class="flex flex-col sm:flex-row sm:items-start sm:justify-between gap-3">
          <div class="min-w-0">
            <NuxtLink v-if="item.hasPage" :to="`/sentences/${item.sentenceId}`" class="hover:underline text-lg" lang="ja">{{ item.now.text }}</NuxtLink>
            <span v-else class="text-lg" lang="ja">{{ item.now.text }}</span>
            <div class="flex flex-wrap items-center gap-2 mt-1 text-xs text-gray-500">
              <Tag v-for="part in changedParts(item)" :key="part" :value="part" />
              <Tag v-if="item.sameText" :value="`Also ${item.sameText} same-text line${item.sameText === 1 ? '' : 's'}`" severity="secondary" />
              <Tag v-if="item.changed" value="The line changed since this was submitted" severity="warn" />
              <Tag v-if="item.removedFromDeck" value="No longer in the deck" severity="warn" />
              <NuxtLink v-if="item.deckId && !item.removedFromDeck" :to="`/decks/media/${item.deckId}/detail`" class="underline">
                {{ item.title }}<template v-if="item.episode"> – {{ item.episode }}</template>
              </NuxtLink>
              <span v-else-if="item.title"
                >{{ item.title }}<template v-if="item.episode"> – {{ item.episode }}</template></span
              >
              <span>by {{ item.submitter }} · {{ new Date(item.createdAt).toLocaleDateString() }}</span>
            </div>
          </div>
          <div class="flex flex-wrap gap-2 shrink-0 self-start">
            <Button label="Approve" icon="pi pi-check" size="small" severity="success" :loading="busy === item.submissionId" @click="approve(item)" />
            <Button label="Reject" icon="pi pi-times" size="small" severity="danger" outlined :disabled="busy === item.submissionId" @click="askReject(item)" />
          </div>
        </div>

        <div class="overflow-x-auto">
          <table class="w-full text-sm border border-surface-200 dark:border-surface-700 rounded">
            <thead>
              <tr class="text-xs uppercase tracking-wide text-gray-500 bg-surface-50 dark:bg-surface-800">
                <th class="text-left font-medium px-2 py-1 w-20"/>
                <th class="text-left font-medium px-2 py-1">Now</th>
                <th class="text-left font-medium px-2 py-1">Proposed</th>
              </tr>
            </thead>
            <tbody class="divide-y divide-surface-200 dark:divide-surface-700">
              <tr v-if="item.proposed.text != null">
                <td class="px-2 py-2 text-gray-500 align-top">Japanese</td>
                <td class="px-2 py-2 align-top" lang="ja">
                  <template v-for="(d, i) in textDiff(item.now.text ?? '', item.proposed.text)" :key="i">
                    <del v-if="d.kind === 'del'" class="bg-red-100 dark:bg-red-900/50">{{ d.text }}</del>
                    <span v-else-if="d.kind === 'same'">{{ d.text }}</span>
                  </template>
                </td>
                <td class="px-2 py-2 align-top" lang="ja">
                  <template v-for="(d, i) in textDiff(item.now.text ?? '', item.proposed.text)" :key="i">
                    <ins v-if="d.kind === 'ins'" class="no-underline bg-green-100 dark:bg-green-900/50">{{ d.text }}</ins>
                    <span v-else-if="d.kind === 'same'">{{ d.text }}</span>
                  </template>
                </td>
              </tr>
              <tr v-if="item.proposed.english != null">
                <td class="px-2 py-2 text-gray-500 align-top">English</td>
                <td class="px-2 py-2 align-top">{{ item.now.english ?? '–' }}</td>
                <td class="px-2 py-2 align-top">{{ item.proposed.english }}</td>
              </tr>
              <tr v-if="item.proposed.startMs != null">
                <td class="px-2 py-2 text-gray-500 align-top">Clip</td>
                <td class="px-2 py-2 align-top">
                  <span class="inline-flex items-center gap-2 font-mono text-xs">
                    <Button
                      icon="pi pi-play"
                      size="small"
                      text
                      rounded
                      aria-label="Play the clip as it is"
                      :disabled="item.now.startMs == null"
                      :loading="playing === `${item.submissionId}-now`"
                      @click="play(item, item.now, `${item.submissionId}-now`)"
                    />
                    {{ formatTime(item.now.startMs) }}–{{ formatTime(item.now.endMs) }}
                  </span>
                </td>
                <td class="px-2 py-2 align-top">
                  <span class="inline-flex items-center gap-2 font-mono text-xs">
                    <Button
                      icon="pi pi-play"
                      size="small"
                      text
                      rounded
                      aria-label="Play the proposed clip"
                      :loading="playing === `${item.submissionId}-new`"
                      @click="play(item, item.proposed, `${item.submissionId}-new`)"
                    />
                    {{ formatTime(item.proposed.startMs) }}–{{ formatTime(item.proposed.endMs) }}
                  </span>
                </td>
              </tr>
              <tr v-if="showsFrame(item)">
                <td class="px-2 py-2 text-gray-500 align-top">Frame</td>
                <td class="px-2 py-2 align-top">
                  <img
                    v-if="frames[`${item.submissionId}-now`]"
                    :src="frames[`${item.submissionId}-now`]"
                    alt="The frame as it is"
                    class="w-40 max-w-full rounded"
                  />
                  <span class="block font-mono text-xs text-gray-500">{{ formatTime(item.now.pictureMs) }}</span>
                </td>
                <td class="px-2 py-2 align-top">
                  <img
                    v-if="frames[`${item.submissionId}-new`]"
                    :src="frames[`${item.submissionId}-new`]"
                    alt="The proposed frame"
                    class="w-40 max-w-full rounded"
                  />
                  <span class="block font-mono text-xs text-gray-500">{{ formatTime(proposedFrame(item)) }}</span>
                </td>
              </tr>
            </tbody>
          </table>
        </div>
      </div>
    </div>

    <Paginator
      v-if="totalCount > rows"
      :first="page * rows"
      :rows="rows"
      :total-records="totalCount"
      @page="
        (e) => {
          page = e.page;
          load();
        }
      "
    />

    <Dialog :visible="rejecting != null" modal header="Reject this fix" class="w-[32rem] max-w-[95vw]" @update:visible="(v) => !v && (rejecting = null)">
      <div class="flex flex-col gap-2">
        <label for="reject-reason" class="text-sm text-gray-600 dark:text-gray-300">Reason, sent to the submitter (optional)</label>
        <InputText id="reject-reason" v-model="reason" maxlength="1000" autofocus @keydown.enter="reject" />
      </div>
      <template #footer>
        <Button label="Cancel" severity="secondary" outlined @click="rejecting = null" />
        <Button label="Reject" severity="danger" :loading="busy === rejecting?.submissionId" @click="reject" />
      </template>
    </Dialog>
  </div>
</template>
