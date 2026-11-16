<script setup lang="ts">
  import Button from 'primevue/button';
  import Paginator from 'primevue/paginator';
  import ProgressSpinner from 'primevue/progressspinner';
  import Select from 'primevue/select';
  import Tag from 'primevue/tag';
  import Textarea from 'primevue/textarea';
  import { useToast } from 'primevue/usetoast';

  type CheckKind = 'english-fit' | 'english-quality' | 'japanese' | 'split';

  interface Finding {
    kind: CheckKind;
    note: string;
    suggestedEnglish?: string | null;
    suggestedText?: string | null;
  }

  interface CheckItem {
    id: number;
    deckId: number;
    title: string;
    episode: string | null;
    position: number;
    text: string;
    english: string | null;
    origin: string | null;
    findings: Finding[];
    checker: string | null;
    checkedAt: string | null;
  }

  /** Sentence checks flagged by Claude Code sessions (GDR-060); nothing changes until Accept. */
  const props = defineProps<{ deckId?: number | null }>();
  const emit = defineEmits<{ count: [n: number] }>();

  const KINDS: { value: CheckKind; label: string }[] = [
    { value: 'english-fit', label: 'English fit' },
    { value: 'english-quality', label: 'English quality' },
    { value: 'japanese', label: 'Japanese' },
    { value: 'split', label: 'Split' },
  ];
  const ORIGINS: Record<string, string> = { subtitles: 'subtitles', ai: 'AI', user: 'shared fix' };

  const { $api } = useNuxtApp();
  const toast = useToast();

  const items = ref<CheckItem[]>([]);
  const total = ref(0);
  const loading = ref(false);
  const kind = ref<CheckKind | null>(null);
  const page = ref(0);
  const rows = 50;
  const busy = ref<number | null>(null);
  /** Per line, the English and Japanese that Accept sends: the suggestion, or what the admin typed over it. */
  const drafts = reactive<Record<number, { english: string; text: string }>>({});

  function kindLabel(value: string) {
    return KINDS.find((k) => k.value === value)?.label ?? value;
  }

  function draftFor(item: CheckItem) {
    const english = item.findings.find((f) => f.suggestedEnglish)?.suggestedEnglish ?? '';
    const text = item.findings.find((f) => f.suggestedText)?.suggestedText ?? '';
    return { english, text };
  }

  /** Whether the line has anything Accept could apply; a split-only flag can only be dismissed. */
  function canAccept(item: CheckItem) {
    const d = drafts[item.id];
    return !!d && ((d.english.trim() && d.english.trim() !== item.english) || (d.text.trim() && d.text.trim() !== item.text));
  }

  async function load() {
    loading.value = true;
    try {
      const params = new URLSearchParams({ offset: String(page.value * rows), limit: String(rows) });
      if (kind.value) params.append('kind', kind.value);
      if (props.deckId) params.append('deckId', String(props.deckId));
      const result = await $api<{ items: CheckItem[]; total: number }>(`admin/sentence-checks?${params}`);
      items.value = result.items;
      total.value = result.total;
      for (const item of result.items) drafts[item.id] = draftFor(item);
      emit('count', result.total);
    } catch (e) {
      toast.add({ severity: 'error', summary: "Couldn't load the checks", detail: extractApiError(e, 'The request failed'), life: 5000 });
    } finally {
      loading.value = false;
    }
  }

  async function resolve(item: CheckItem, how: 'accept' | 'dismiss') {
    busy.value = item.id;
    try {
      const d = drafts[item.id]!;
      const body =
        how === 'accept'
          ? {
              english: d.english.trim() && d.english.trim() !== item.english ? d.english.trim() : null,
              text: d.text.trim() && d.text.trim() !== item.text ? d.text.trim() : null,
            }
          : {};
      await $api(`admin/sentence-checks/${item.id}/${how}`, { method: 'POST', body });
      toast.add({ severity: 'success', summary: how === 'accept' ? 'Accepted' : 'Dismissed', life: 2000 });
      if (items.value.length === 1 && page.value > 0) page.value--;
      await load();
    } catch (e) {
      toast.add({ severity: 'error', summary: `Couldn't ${how}`, detail: extractApiError(e, 'The request failed'), life: 5000 });
    } finally {
      busy.value = null;
    }
  }

  watch([kind, () => props.deckId], () => {
    page.value = 0;
    load();
  });

  onMounted(load);
</script>

<template>
  <div>
    <p class="text-sm text-gray-600 dark:text-gray-300 mb-4">
      Lines a Claude Code check flagged. Accept applies the suggestion, or what you type over it: Japanese and subtitle English become the shared line's
      fix, and AI English replaces the cached translation for every line with that text. Dismiss keeps the line as it is. Nothing changes until you accept.
    </p>

    <div class="mb-4 flex flex-wrap items-center gap-4">
      <Tag severity="warn" class="text-base px-3 py-1">{{ total }} flagged line{{ total === 1 ? '' : 's' }}</Tag>
      <Select v-model="kind" :options="KINDS" option-label="label" option-value="value" placeholder="Any kind" show-clear class="w-56" aria-label="Kind" />
    </div>

    <div v-if="loading && items.length === 0" class="flex justify-center py-12">
      <ProgressSpinner style="width: 50px; height: 50px" stroke-width="4" />
    </div>

    <p v-else-if="items.length === 0" class="text-gray-500 py-8 text-center">No flagged checks.</p>

    <div v-else class="flex flex-col divide-y divide-surface-200 dark:divide-surface-700" :class="{ 'opacity-60': loading }">
      <div v-for="item in items" :key="item.id" class="py-4 flex flex-col gap-2">
        <div class="flex flex-wrap items-center gap-2 text-xs text-gray-500">
          <NuxtLink :to="`/decks/media/${item.deckId}/detail`" class="underline">
            {{ item.title }}<template v-if="item.episode"> – {{ item.episode }}</template>
          </NuxtLink>
          <span>line {{ item.position }}</span>
          <Tag v-for="(f, i) in item.findings" :key="i" :value="kindLabel(f.kind)" severity="secondary" />
          <NuxtLink :to="`/sentences/${item.id}`" class="underline ml-auto"><i class="pi pi-play-circle mr-1" />Sentence page</NuxtLink>
        </div>

        <div class="text-lg" lang="ja">{{ item.text }}</div>
        <div class="text-sm">
          <span class="text-xs text-gray-500">Now ({{ ORIGINS[item.origin ?? ''] ?? item.origin }}):</span>
          {{ item.english }}
        </div>

        <ul class="flex flex-col gap-1">
          <li v-for="(f, i) in item.findings" :key="i" class="text-sm text-gray-600 dark:text-gray-300">
            <span class="text-xs text-gray-500">{{ kindLabel(f.kind) }}:</span> {{ f.note }}
          </li>
        </ul>

        <div v-if="drafts[item.id]" class="grid gap-2 sm:grid-cols-2">
          <label class="flex flex-col gap-1 text-xs text-gray-500">
            Japanese
            <Textarea v-model="drafts[item.id]!.text" rows="2" auto-resize :placeholder="item.text" lang="ja" />
          </label>
          <label class="flex flex-col gap-1 text-xs text-gray-500">
            English
            <Textarea v-model="drafts[item.id]!.english" rows="2" auto-resize :placeholder="item.english ?? ''" />
          </label>
        </div>

        <div class="flex flex-wrap gap-2">
          <Button
            label="Accept"
            icon="pi pi-check"
            size="small"
            severity="success"
            :disabled="!canAccept(item) || (busy !== null && busy !== item.id)"
            :loading="busy === item.id"
            @click="resolve(item, 'accept')"
          />
          <Button
            label="Dismiss"
            icon="pi pi-times"
            size="small"
            severity="secondary"
            outlined
            :disabled="busy !== null"
            @click="resolve(item, 'dismiss')"
          />
          <Button label="Open fix page" icon="pi pi-pencil" size="small" text @click="navigateTo(`/sentences/${item.id}/fix`)" />
        </div>
      </div>
    </div>

    <Paginator
      v-if="total > rows"
      :first="page * rows"
      :rows="rows"
      :total-records="total"
      class="mt-4"
      @page="
        (e) => {
          page = e.page;
          load();
        }
      "
    />
  </div>
</template>
