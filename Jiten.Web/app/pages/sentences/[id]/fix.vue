<script setup lang="ts">
  import Checkbox from 'primevue/checkbox';
  import InputText from 'primevue/inputtext';
  import Textarea from 'primevue/textarea';

  definePageMeta({ middleware: ['auth'] });
  useHead({ title: 'Fix a sentence - Jiten' });
  useRobotsRule({ noindex: true, follow: false });

  /** One version of a line's parts; null parts aren't set at that level. Times are in subtitle time, padding included. */
  interface FixParts {
    text: string | null;
    english: string | null;
    startMs: number | null;
    endMs: number | null;
    pictureMs: number | null;
  }
  interface FixCue {
    startMs: number;
    endMs: number;
    text: string;
    english: string | null;
    inClip: boolean;
  }
  interface SentenceFixEditor {
    sentenceId: number;
    text: string;
    title: string | null;
    episode: string | null;
    deckId: number | null;
    hasVideo: boolean;
    hasPage: boolean;
    original: FixParts;
    shared: FixParts | null;
    mine: FixParts | null;
    cues: FixCue[];
    sameText: { sentenceId: number; title: string | null; episode: string | null }[];
    mined: { minedSentenceId: number; expression: string; removable: boolean }[];
    flag: { sentenceFlagId: number; reason: string | null; note: string | null } | null;
  }

  const DEFAULT_PAD_MS = 250;
  const reasonLabels: Record<string, string> = { japanese: 'Japanese text', english: 'English', media: 'Audio or screenshot', parsing: 'Parsing' };

  const route = useRoute();
  const toast = useToast();
  const confirm = useConfirm();
  const authStore = useAuthStore();
  const { $api } = useNuxtApp();
  const { forget: forgetEnglish } = useSentenceTranslation();
  const sentenceFlags = useSentenceFlags();
  const sentenceId = computed(() => Number(route.params.id));

  const editor = ref<SentenceFixEditor | null>(null);
  const missing = ref(false);
  const saving = ref(false);

  // The form: what the user's fix will be. A part equal to the base (shared fix, else original) isn't saved.
  const text = ref('');
  const english = ref('');
  const startMs = ref<number | null>(null);
  const endMs = ref<number | null>(null);
  /** Null follows the clip's middle (or the shared frame). */
  const pictureMs = ref<number | null>(null);
  const sameTextIds = ref<number[]>([]);

  const base = computed(() => {
    const e = editor.value;
    return {
      text: e?.shared?.text ?? e?.original.text ?? '',
      english: e?.shared?.english ?? e?.original.english ?? '',
      startMs: e?.shared?.startMs ?? e?.original.startMs ?? null,
      endMs: e?.shared?.endMs ?? e?.original.endMs ?? null,
      pictureMs: e?.shared?.pictureMs ?? null,
    };
  });

  async function load() {
    missing.value = false;
    try {
      const e = await $api<SentenceFixEditor>(`sentences/${sentenceId.value}/fix`);
      editor.value = e;
      text.value = e.mine?.text ?? e.shared?.text ?? e.original.text ?? '';
      english.value = e.mine?.english ?? e.shared?.english ?? e.original.english ?? '';
      startMs.value = e.mine?.startMs ?? base.value.startMs;
      endMs.value = e.mine?.endMs ?? base.value.endMs;
      pictureMs.value = e.mine?.pictureMs ?? null;
      suggestion.value = null;
      if (e.hasVideo) loadPicture();
    } catch {
      editor.value = null;
      missing.value = true;
    }
  }

  // ---- Where each part comes from ----
  type Origin = 'original' | 'shared' | 'mine';
  function origin(changed: boolean, shared: boolean): Origin {
    return changed ? 'mine' : shared ? 'shared' : 'original';
  }
  const textChanged = computed(() => text.value.trim() !== base.value.text);
  const englishChanged = computed(() => english.value.trim() !== (base.value.english ?? '') && english.value.trim() !== '');
  const rangeChanged = computed(() => startMs.value !== base.value.startMs || endMs.value !== base.value.endMs);
  const pictureChanged = computed(() => pictureMs.value != null && pictureMs.value !== base.value.pictureMs);
  const origins = computed(() => ({
    text: origin(textChanged.value, editor.value?.shared?.text != null),
    english: origin(englishChanged.value, editor.value?.shared?.english != null),
    audio: origin(rangeChanged.value, editor.value?.shared?.startMs != null),
    picture: origin(pictureChanged.value, editor.value?.shared?.pictureMs != null),
  }));
  const originLabels: Record<Origin, string> = { original: 'original', shared: 'shared fix', mine: 'your fix' };
  const originSeverity: Record<Origin, string> = { original: 'secondary', shared: 'warn', mine: 'success' };
  const anyChange = computed(() => textChanged.value || englishChanged.value || rangeChanged.value || pictureChanged.value);
  const hadFix = computed(() => editor.value?.mine != null);

  // ---- Audio range ----
  const length = computed(() => (startMs.value != null && endMs.value != null ? endMs.value - startMs.value : 0));

  function covered(cue: FixCue): boolean {
    if (startMs.value == null || endMs.value == null) return false;
    const overlap = Math.min(cue.endMs, endMs.value) - Math.max(cue.startMs, startMs.value);
    return overlap >= 0.5 * Math.max(1, cue.endMs - cue.startMs);
  }

  /** Ticking cues sets the clip to them, with the default padding. */
  function toggleCue(cue: FixCue) {
    const cues = editor.value?.cues ?? [];
    const ticked = cues.filter((c) => (c === cue ? !covered(c) : covered(c)));
    if (ticked.length === 0) return;
    startMs.value = Math.max(0, Math.min(...ticked.map((c) => c.startMs)) - DEFAULT_PAD_MS);
    endMs.value = Math.max(...ticked.map((c) => c.endMs)) + DEFAULT_PAD_MS;
    rangeEdited();
  }

  function nudge(end: 'start' | 'end', deltaMs: number) {
    if (startMs.value == null || endMs.value == null) return;
    if (end === 'start') startMs.value = Math.max(0, Math.min(startMs.value + deltaMs, endMs.value - 100));
    else endMs.value = Math.max(endMs.value + deltaMs, startMs.value + 100);
    rangeEdited();
  }

  function resetAudio() {
    startMs.value = base.value.startMs;
    endMs.value = base.value.endMs;
    rangeEdited();
  }

  let player: HTMLAudioElement | null = null;
  const playing = ref(false);
  async function playRange() {
    if (startMs.value == null || endMs.value == null) return;
    player?.pause();
    playing.value = true;
    try {
      const blob = await $api<Blob>(`sentence-media/${sentenceId.value}/audio`, {
        responseType: 'blob',
        query: { startMs: startMs.value, endMs: endMs.value },
      });
      const url = URL.createObjectURL(blob);
      player = new Audio(url);
      player.addEventListener('ended', () => URL.revokeObjectURL(url));
      await player.play();
    } catch (e) {
      toast.add({ severity: 'error', summary: "Couldn't cut the clip", detail: extractApiError(e, ''), life: 5000 });
    } finally {
      playing.value = false;
    }
  }

  // ---- English suggested for a changed clip ----
  const suggestion = ref<string | null>(null);
  let suggestTimer: ReturnType<typeof setTimeout> | null = null;
  function rangeEdited() {
    if (suggestTimer) clearTimeout(suggestTimer);
    if (!pictureMs.value) loadPicture();
    suggestTimer = setTimeout(suggestEnglish, 400);
  }
  async function suggestEnglish() {
    if (startMs.value == null || endMs.value == null || !editor.value?.hasVideo) return;
    try {
      const { text: found } = await $api<{ text: string | null }>(`sentences/${sentenceId.value}/fix/english`, {
        query: { startMs: startMs.value, endMs: endMs.value },
      });
      suggestion.value = found && found !== english.value.trim() ? found : null;
    } catch {
      suggestion.value = null;
    }
  }

  // ---- Screenshot ----
  const frameMs = computed(() => pictureMs.value ?? base.value.pictureMs ?? (startMs.value != null && endMs.value != null ? Math.round((startMs.value + endMs.value) / 2) : null));
  const pictureUrl = ref<string | null>(null);
  const pictureLoading = ref(false);
  let pictureRequest = 0;
  async function loadPicture() {
    if (frameMs.value == null) return;
    const request = ++pictureRequest;
    pictureLoading.value = true;
    try {
      const blob = await $api<Blob>(`sentence-media/${sentenceId.value}/picture`, { responseType: 'blob', query: { atMs: frameMs.value } });
      if (request !== pictureRequest) return;
      if (pictureUrl.value) URL.revokeObjectURL(pictureUrl.value);
      pictureUrl.value = URL.createObjectURL(blob);
    } catch {
      // The previous frame stays
    } finally {
      if (request === pictureRequest) pictureLoading.value = false;
    }
  }
  function stepFrame(deltaMs: number) {
    if (frameMs.value == null) return;
    pictureMs.value = Math.max(0, frameMs.value + deltaMs);
    loadPicture();
  }
  function resetPicture() {
    pictureMs.value = null;
    loadPicture();
  }

  // ---- Saving ----
  function request() {
    return {
      text: textChanged.value ? text.value.trim() : null,
      english: englishChanged.value ? english.value.trim() : null,
      startMs: rangeChanged.value ? startMs.value : null,
      endMs: rangeChanged.value ? endMs.value : null,
      pictureMs: pictureChanged.value ? pictureMs.value : null,
    };
  }

  function done(summary: string) {
    forgetEnglish(sentenceId.value);
    // Saving closed the flag on the line
    sentenceFlags.load().catch(() => {});
    const mined = (editor.value?.mined.length ?? 0) > 0;
    toast.add({ severity: 'success', summary, detail: mined ? 'Your cards update on the next Sync.' : undefined, life: 4000 });
    navigateTo(editor.value?.hasPage ? `/sentences/${sentenceId.value}` : '/mined');
  }

  async function save() {
    saving.value = true;
    try {
      await $api(`sentences/${sentenceId.value}/fix`, { method: 'PUT', body: request() });
      done(anyChange.value ? 'Fix saved' : 'Fix reset');
    } catch (e) {
      toast.add({ severity: 'error', summary: "Couldn't save the fix", detail: extractApiError(e, ''), life: 6000 });
    } finally {
      saving.value = false;
    }
  }

  async function submit() {
    saving.value = true;
    try {
      await $api(`sentences/${sentenceId.value}/fix`, { method: 'PUT', body: request() });
      await $api(`sentences/${sentenceId.value}/fix/submit`, { method: 'POST', body: { sameTextIds: sameTextIds.value } });
      done('Fixed for everyone');
    } catch (e) {
      toast.add({ severity: 'error', summary: "Couldn't submit the fix", detail: extractApiError(e, ''), life: 6000 });
    } finally {
      saving.value = false;
    }
  }

  function remove(mined: SentenceFixEditor['mined'][number]) {
    confirm.require({
      header: `Remove from ${mined.expression}`,
      message: `Take this sentence off ${mined.expression}'s list? The next Sync takes it off the card too.`,
      acceptProps: { label: 'Remove', severity: 'danger' },
      rejectProps: { label: 'Cancel', severity: 'secondary', outlined: true },
      accept: async () => {
        try {
          await $api(`mined/sentences/${mined.minedSentenceId}`, { method: 'DELETE' });
          toast.add({ severity: 'success', summary: `Removed from ${mined.expression}`, life: 4000 });
          await load();
        } catch (e) {
          toast.add({ severity: 'error', summary: "Couldn't remove it", detail: extractApiError(e, ''), life: 6000 });
        }
      },
    });
  }

  function formatTime(ms: number | null) {
    if (ms == null) return '–';
    const seconds = ms / 1000;
    return `${Math.floor(seconds / 60)}:${(seconds % 60).toFixed(1).padStart(4, '0')}`;
  }

  onBeforeUnmount(() => {
    player?.pause();
    if (pictureUrl.value) URL.revokeObjectURL(pictureUrl.value);
  });

  if (import.meta.client) watch(sentenceId, load, { immediate: true });
</script>

<template>
  <div class="flex flex-col gap-4 max-w-4xl mx-auto w-full pb-24">
    <Card v-if="missing">
      <template #content>
        <p class="text-gray-600 dark:text-gray-300">This sentence doesn't exist, or it's private.</p>
      </template>
    </Card>
    <div v-else-if="!editor" class="h-48 rounded bg-surface-100 dark:bg-surface-800 animate-pulse" />

    <template v-else>
      <Card>
        <template #content>
          <div class="flex flex-col gap-2">
            <div class="text-sm text-gray-500 dark:text-gray-400 flex flex-wrap gap-x-2">
              <NuxtLink v-if="editor.hasPage" :to="`/sentences/${editor.sentenceId}`" class="text-primary-600 hover:underline">← Back to the sentence</NuxtLink>
              <span v-if="editor.title">{{ editor.title }}</span>
              <span v-if="editor.episode">· {{ editor.episode }}</span>
              <span v-if="editor.mined.length">· mined for {{ editor.mined.map((m) => m.expression).join(', ') }}</span>
            </div>
            <h1 class="text-2xl" lang="ja">{{ editor.text }}</h1>
            <p v-if="editor.flag" class="text-sm text-gray-600 dark:text-gray-300">
              <i class="pi pi-flag-fill text-red-500 mr-1" aria-hidden="true" />
              You flagged this line<template v-if="editor.flag.reason">: <b>{{ reasonLabels[editor.flag.reason] ?? editor.flag.reason }}</b></template
              ><template v-if="editor.flag.note">, “{{ editor.flag.note }}”</template>. Saving a fix closes the flag.
            </p>
          </div>
        </template>
      </Card>

      <Card v-if="editor.hasVideo">
        <template #content>
          <div class="flex flex-col gap-3">
            <div class="flex items-center gap-2 flex-wrap">
              <h2 class="text-lg font-semibold">Audio range</h2>
              <Tag :value="originLabels[origins.audio]" :severity="originSeverity[origins.audio]" />
              <Button v-if="rangeChanged" label="Reset to original" size="small" text class="ml-auto" @click="resetAudio" />
            </div>
            <p class="text-sm text-gray-500 dark:text-gray-400">Tick the subtitle lines the clip should cover, then nudge the ends.</p>
            <div class="flex flex-col gap-1">
              <label
                v-for="cue in editor.cues"
                :key="cue.startMs"
                class="grid grid-cols-[1.5rem_4rem_1fr] gap-2 items-start rounded px-2 py-1.5 cursor-pointer"
                :class="covered(cue) ? 'bg-primary-50 dark:bg-primary-950' : ''"
              >
                <Checkbox :model-value="covered(cue)" binary :input-id="`cue-${cue.startMs}`" @update:model-value="toggleCue(cue)" />
                <span class="text-sm tabular-nums text-gray-500 dark:text-gray-400">{{ formatTime(cue.startMs) }}</span>
                <span class="min-w-0">
                  <span lang="ja" class="whitespace-pre-line">{{ cue.text }}</span>
                  <span v-if="cue.english" class="block text-sm text-gray-500 dark:text-gray-400">{{ cue.english }}</span>
                </span>
              </label>
            </div>
            <div class="grid grid-cols-[auto_1fr] gap-x-3 gap-y-2 items-center tabular-nums">
              <span class="text-sm text-gray-500 dark:text-gray-400">Start</span>
              <div class="flex items-center gap-1 flex-wrap">
                <Button label="−0.5" size="small" severity="secondary" outlined @click="nudge('start', -500)" />
                <Button label="−0.1" size="small" severity="secondary" outlined @click="nudge('start', -100)" />
                <b class="w-16 text-center">{{ formatTime(startMs) }}</b>
                <Button label="+0.1" size="small" severity="secondary" outlined @click="nudge('start', 100)" />
                <Button label="+0.5" size="small" severity="secondary" outlined @click="nudge('start', 500)" />
              </div>
              <span class="text-sm text-gray-500 dark:text-gray-400">End</span>
              <div class="flex items-center gap-1 flex-wrap">
                <Button label="−0.5" size="small" severity="secondary" outlined @click="nudge('end', -500)" />
                <Button label="−0.1" size="small" severity="secondary" outlined @click="nudge('end', -100)" />
                <b class="w-16 text-center">{{ formatTime(endMs) }}</b>
                <Button label="+0.1" size="small" severity="secondary" outlined @click="nudge('end', 100)" />
                <Button label="+0.5" size="small" severity="secondary" outlined @click="nudge('end', 500)" />
              </div>
            </div>
            <div class="flex items-center gap-3 flex-wrap">
              <Button label="Play range" icon="pi pi-play" size="small" :loading="playing" @click="playRange" />
              <span class="text-sm text-gray-500 dark:text-gray-400">{{ (length / 1000).toFixed(1) }} s, padding included</span>
            </div>
          </div>
        </template>
      </Card>

      <div class="grid gap-4" :class="editor.hasVideo ? 'md:grid-cols-2' : ''">
        <Card v-if="editor.hasVideo">
          <template #content>
            <div class="flex flex-col gap-3">
              <div class="flex items-center gap-2 flex-wrap">
                <h2 class="text-lg font-semibold">Screenshot</h2>
                <Tag :value="originLabels[origins.picture]" :severity="originSeverity[origins.picture]" />
                <Button v-if="pictureChanged" label="Reset" size="small" text class="ml-auto" @click="resetPicture" />
              </div>
              <div class="relative aspect-video rounded overflow-hidden bg-surface-100 dark:bg-surface-800">
                <img v-if="pictureUrl" :src="pictureUrl" alt="The screenshot's frame" class="w-full h-full object-contain" />
                <i v-if="pictureLoading" class="pi pi-spin pi-spinner absolute right-2 top-2 text-white drop-shadow" aria-label="Loading the frame" />
              </div>
              <div class="flex items-center gap-1 flex-wrap tabular-nums">
                <Button label="−2 s" size="small" severity="secondary" outlined @click="stepFrame(-2000)" />
                <Button label="−0.5 s" size="small" severity="secondary" outlined @click="stepFrame(-500)" />
                <b class="w-16 text-center">{{ formatTime(frameMs) }}</b>
                <Button label="+0.5 s" size="small" severity="secondary" outlined @click="stepFrame(500)" />
                <Button label="+2 s" size="small" severity="secondary" outlined @click="stepFrame(2000)" />
              </div>
            </div>
          </template>
        </Card>

        <Card>
          <template #content>
            <div class="flex flex-col gap-3">
              <div class="flex items-center gap-2 flex-wrap">
                <h2 class="text-lg font-semibold">Japanese</h2>
                <Tag :value="originLabels[origins.text]" :severity="originSeverity[origins.text]" />
                <Button v-if="textChanged" label="Reset" size="small" text class="ml-auto" @click="text = base.text" />
              </div>
              <label for="fix-text" class="text-sm text-gray-500 dark:text-gray-400">The card's text. Saving reads its furigana again and finds your word in it.</label>
              <Textarea id="fix-text" v-model="text" rows="2" auto-resize class="text-lg" lang="ja" />
              <p v-if="editor.original.text !== base.text" class="text-sm text-gray-500 dark:text-gray-400">
                Subtitle: <span lang="ja">{{ editor.original.text }}</span>
              </p>
            </div>
          </template>
        </Card>
      </div>

      <Card>
        <template #content>
          <div class="flex flex-col gap-3">
            <div class="flex items-center gap-2 flex-wrap">
              <h2 class="text-lg font-semibold">English</h2>
              <Tag :value="originLabels[origins.english]" :severity="originSeverity[origins.english]" />
              <Button v-if="englishChanged" label="Reset" size="small" text class="ml-auto" @click="english = base.english ?? ''" />
            </div>
            <label for="fix-english" class="text-sm text-gray-500 dark:text-gray-400">
              {{ editor.shared?.english ? 'A shared fix; your edit overrides it for you.' : 'From the subtitles or the AI. Your edit overrides it for you.' }}
            </label>
            <InputText id="fix-english" v-model="english" />
            <div v-if="suggestion" class="flex items-center gap-3 flex-wrap rounded bg-primary-50 dark:bg-primary-950 px-3 py-2 text-sm">
              <span>Subtitles for the new clip: <b>“{{ suggestion }}”</b></span>
              <Button label="Use it" size="small" @click="(english = suggestion!), (suggestion = null)" />
            </div>
          </div>
        </template>
      </Card>

      <Card v-if="authStore.isAdmin && editor.deckId != null">
        <template #content>
          <div class="flex flex-col gap-3">
            <div class="flex items-center gap-2 flex-wrap">
              <h2 class="text-lg font-semibold">Submit for everyone</h2>
              <span class="text-sm text-gray-500 dark:text-gray-400">admins only for now</span>
            </div>
            <p class="text-sm text-gray-500 dark:text-gray-400">
              Submit makes your fix the shared line and closes everyone's flags on it. The words the line counts for are read again from the new text.
            </p>
            <template v-if="editor.sameText.length && textChanged">
              <p class="text-sm text-gray-500 dark:text-gray-400">The same text appears elsewhere. Tick the lines to give the new text too:</p>
              <label v-for="line in editor.sameText" :key="line.sentenceId" class="flex items-center gap-2 text-sm">
                <Checkbox v-model="sameTextIds" :value="line.sentenceId" :input-id="`same-${line.sentenceId}`" />
                <span>{{ [line.title, line.episode].filter(Boolean).join(' · ') }}</span>
              </label>
            </template>
          </div>
        </template>
      </Card>

      <Card v-if="editor.mined.length">
        <template #content>
          <div class="flex flex-col gap-3">
            <h2 class="text-lg font-semibold">Remove from a word</h2>
            <p class="text-sm text-gray-500 dark:text-gray-400">Takes the sentence off the word's list, and off its card on the next Sync.</p>
            <div v-for="m in editor.mined" :key="m.minedSentenceId" class="flex items-center gap-3 flex-wrap">
              <span lang="ja" class="font-semibold">{{ m.expression }}</span>
              <Button label="Remove" size="small" severity="danger" outlined :disabled="!m.removable" @click="remove(m)" />
              <span v-if="!m.removable" class="text-sm text-gray-500 dark:text-gray-400">
                It's the card's only sentence, or the one its Anki note was made with. Flag it to keep it off the card.
              </span>
            </div>
          </div>
        </template>
      </Card>

      <div
        class="sticky bottom-0 z-10 flex items-center gap-2 flex-wrap rounded border border-surface-200 dark:border-surface-700 bg-white dark:bg-surface-900 px-4 py-3 shadow"
      >
        <span class="text-sm text-gray-500 dark:text-gray-400 mr-auto">
          {{ editor.mined.length ? 'Your cards update on the next Sync.' : 'Only you see your fix.' }}
        </span>
        <Button label="Cancel" severity="secondary" outlined @click="navigateTo(editor.hasPage ? `/sentences/${editor.sentenceId}` : '/mined')" />
        <Button
          v-if="authStore.isAdmin && editor.deckId != null"
          label="Submit"
          severity="secondary"
          :disabled="!anyChange"
          :loading="saving"
          @click="submit"
        />
        <Button :label="anyChange || !hadFix ? 'Save my fix' : 'Reset my fix'" :disabled="!anyChange && !hadFix" :loading="saving" @click="save" />
      </div>
    </template>
  </div>
</template>
