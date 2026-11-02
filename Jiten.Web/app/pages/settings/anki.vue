<script setup lang="ts">
  import { ankiConnect, ankiConnectMulti, DEFAULT_ANKI_CONNECT_URL } from '~/composables/useAnkiConnect';
  import { ankiSearchValue, newSetup } from '~/utils/ankiSetups';
  import type { MediaType } from '~/types/enums';
  import { getMediaTypeText } from '~/utils/mediaTypeMapper';
  import { useToast } from 'primevue/usetoast';

  definePageMeta({ middleware: ['auth'] });
  useHead({ title: 'Anki - Settings - Jiten' });

  const anki = useAnkiSetups();
  const { device, setKey, setUrl } = useAnkiDevice();
  const toast = useToast();

  const cardSettingsVisible = ref(false);
  const ankiUrl = ref(device.value.url);
  const ankiKey = ref(device.value.key);

  /** Anki's note types without a setup, with their note counts. */
  const others = ref<{ name: string; notes: number }[] | null>(null);
  const ankiError = ref<string | null>(null);
  const adding = ref<string | null>(null);

  async function loadOthers() {
    ankiError.value = null;
    try {
      const names = await ankiConnect<string[]>('modelNames');
      const free = names.filter((n) => !anki.setupFor(n)).sort((a, b) => a.localeCompare(b));
      // One request: AnkiConnect answers one at a time and drops connections under a burst.
      const counts = await ankiConnectMulti<number[]>(free.map((n) => ({ action: 'findNotes', params: { query: `"note:${ankiSearchValue(n)}"` } })));
      others.value = free.map((name, i) => ({ name, notes: counts[i]!.length })).sort((a, b) => b.notes - a.notes || a.name.localeCompare(b.name));
    } catch (e) {
      others.value = null;
      ankiError.value = e instanceof TypeError ? 'unreachable' : extractApiError(e, 'AnkiConnect refused');
    }
  }

  onMounted(async () => {
    await anki.load().catch(() => {});
    await loadOthers();
  });

  async function add(name: string) {
    adding.value = name;
    try {
      const fields = await ankiConnect<string[]>('modelFieldNames', { modelName: name });
      await anki.save(newSetup(name, fields));
      others.value = others.value?.filter((o) => o.name !== name) ?? null;
    } catch (e) {
      toast.add({ severity: 'error', summary: `Couldn't add ${name}`, detail: extractApiError(e, 'Failed'), life: 5000 });
    } finally {
      adding.value = null;
    }
  }

  function saveDevice() {
    setUrl(ankiUrl.value);
    setKey(ankiKey.value);
    toast.add({ severity: 'success', summary: 'Saved on this device', life: 2000 });
    loadOthers();
  }

  watch(
    () => anki.setups.value.length,
    () => loadOthers()
  );

  const targets = computed(() => anki.cardTargets.value.targets);
  const configured = computed(() => targets.value.some((t) => t.deckName));
  /** Note types whose cards get mined sentences without asking. */
  const autoTypes = computed(() => anki.miningSetups.value.filter((s) => s.mining?.existing === 'auto').map((s) => s.modelName));

  /** What a target's rule takes, in words: "Anime, Drama or ゆるキャン△". */
  function ruleText(t: (typeof targets.value)[number]): string {
    const names = [...(t.when?.mediaTypes ?? []).map((m) => getMediaTypeText(m as MediaType)), ...(t.when?.titles ?? []).map((x) => x.title)];
    return names.length > 1 ? `${names.slice(0, -1).join(', ')} or ${names[names.length - 1]}` : (names[0] ?? '');
  }
  const origin = import.meta.client ? window.location.origin : '';
</script>

<template>
  <div class="container mx-auto flex max-w-4xl flex-col gap-4 p-2 md:p-4">
    <div class="flex items-center gap-2">
      <NuxtLink to="/settings">
        <Button icon="pi pi-arrow-left" severity="secondary" text rounded aria-label="Settings" />
      </NuxtLink>
      <h1 class="text-2xl font-bold">Anki</h1>
    </div>

    <Message v-if="ankiError === 'unreachable'" severity="warn" :closable="false">
      Couldn't reach AnkiConnect at {{ device.url }}. Check that Anki is open and that <code>{{ origin }}</code> is in AnkiConnect's
      <code>webCorsOriginList</code>. Your setups below are saved on your account and can still be edited.
    </Message>
    <Message v-else-if="ankiError" severity="warn" :closable="false">AnkiConnect: {{ ankiError }}</Message>

    <Card>
      <template #title>New cards</template>
      <template #content>
        <div class="flex flex-wrap items-center justify-between gap-3 text-sm">
          <div v-if="configured" class="flex flex-col gap-1 min-w-0" data-testid="card-targets">
            <div v-for="(t, i) in targets" :key="i">
              <template v-if="i < targets.length - 1">Lines from {{ ruleText(t) }} go</template>
              <template v-else-if="targets.length > 1">Everything else goes</template>
              <template v-else>Mined sentences go</template>
              to <b>{{ t.deckName }}</b> as <b>{{ t.modelName }}</b> notes<template v-if="t.tags">, tagged {{ t.tags }}</template>.
            </div>
            <div v-if="autoTypes.length" class="text-gray-500 dark:text-gray-400">
              Words already on a <b>{{ autoTypes.join(' or ') }}</b> card get the sentence added to it.
            </div>
          </div>
          <div v-else class="text-gray-500 dark:text-gray-400">No deck or note type chosen yet.</div>
          <Button label="Card settings" icon="pi pi-cog" size="small" severity="secondary" outlined @click="cardSettingsVisible = true" />
        </div>
      </template>
    </Card>

    <section class="flex flex-col gap-3" aria-labelledby="synced-types">
      <h2 id="synced-types" class="text-lg font-semibold">Synced note types</h2>
      <p class="text-sm text-gray-600 dark:text-gray-300">
        Sync (on <NuxtLink to="/mined" class="underline">Mined words</NuxtLink>) reads and writes these note types' notes, through each one's field roles. Note
        types without a setup are left alone.
      </p>
      <p v-if="anki.loaded.value && !anki.setups.value.length" class="text-sm text-gray-500">None yet. Add one below.</p>
      <AnkiNoteTypeSetupCard v-for="s in anki.setups.value" :key="s.modelName" :setup="s" />
    </section>

    <section class="flex flex-col gap-2" aria-labelledby="other-types">
      <h2 id="other-types" class="text-lg font-semibold">Other note types in Anki</h2>
      <p v-if="!others && !ankiError" class="text-sm text-gray-500">Reading Anki…</p>
      <div v-else-if="others" class="grid grid-cols-1 gap-2 sm:grid-cols-2">
        <div
          v-for="o in others"
          :key="o.name"
          class="flex items-center justify-between gap-2 rounded border border-surface-200 px-3 py-2 text-sm dark:border-surface-700"
        >
          <span class="min-w-0 truncate"
            >{{ o.name }} <span class="text-gray-500">· {{ o.notes.toLocaleString() }} notes</span></span
          >
          <Button label="Add" icon="pi pi-plus" size="small" text :loading="adding === o.name" @click="add(o.name)" />
        </div>
      </div>
    </section>

    <Card>
      <template #title>This device</template>
      <template #content>
        <div class="grid grid-cols-1 gap-3 sm:grid-cols-2">
          <div class="flex flex-col gap-1">
            <label for="anki-url" class="text-sm text-gray-500 dark:text-gray-400">AnkiConnect address</label>
            <InputText id="anki-url" v-model="ankiUrl" :placeholder="DEFAULT_ANKI_CONNECT_URL" />
          </div>
          <div class="flex flex-col gap-1">
            <label for="anki-key" class="text-sm text-gray-500 dark:text-gray-400">AnkiConnect API key (only if you set one)</label>
            <InputText id="anki-key" v-model="ankiKey" type="password" autocomplete="off" />
          </div>
        </div>
        <div class="mt-3 flex justify-end">
          <Button label="Save on this device" size="small" outlined @click="saveDevice" />
        </div>
      </template>
    </Card>

    <MiningCardSettingsDialog v-model:visible="cardSettingsVisible" />
  </div>
</template>
