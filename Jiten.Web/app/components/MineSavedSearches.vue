<script setup lang="ts">
  import { useToast } from 'primevue/usetoast';
  import { MAX_MINE_SEARCHES, MAX_MINE_SEARCH_NAME, nameProblem, sameQuery, searchQuery, type MineSearch } from '~/utils/mineSearches';

  /**
   * The Mine page's saved searches: pick one to load its filters, save the current filters over it or as a new one, or
   * delete it. Stored on the server (`useMineSearches`), so the phone and the PC share them.
   */
  const props = defineProps<{
    /** The page's filters as URL keys (`filterQuery()`). */
    query: Record<string, string>;
  }>();

  /** The chosen search's id. */
  const selected = defineModel<string | null>('selected', { default: null });

  const emit = defineEmits<{ apply: [query: Record<string, string>] }>();

  const toast = useToast();
  const { searches, load, update } = useMineSearches();

  const chosen = computed(() => searches.value.find((s) => s.id === selected.value) ?? null);
  const changed = computed(() => !!chosen.value && !sameQuery(chosen.value.query, props.query));
  const options = computed(() => [...searches.value].sort((a, b) => a.name.localeCompare(b.name)));

  const naming = ref(false);
  const draftName = ref('');
  const problem = computed(() => nameProblem(draftName.value, searches.value));
  const busy = ref(false);
  const deleting = ref(false);

  onMounted(async () => {
    try {
      await load();
    } catch {
      toast.add({ severity: 'warn', summary: "Couldn't load your saved searches.", life: 4000, group: 'bottom' });
      return;
    }
    // A bookmarked or reloaded page whose filters are a saved search shows it as chosen.
    if (selected.value == null) selected.value = searches.value.find((s) => sameQuery(s.query, props.query))?.id ?? null;
  });

  function choose(id: string | null) {
    selected.value = id;
    const search = searches.value.find((s) => s.id === id);
    if (search) emit('apply', { ...search.query });
  }

  async function write(change: (list: MineSearch[]) => MineSearch[], success: string): Promise<boolean> {
    busy.value = true;
    try {
      await update(change);
      toast.add({ severity: 'success', summary: success, life: 2500, group: 'bottom' });
      return true;
    } catch (e) {
      const message = (e as { data?: { message?: string } })?.data?.message;
      toast.add({ severity: 'error', summary: "Couldn't save your searches", detail: message, life: 5000, group: 'bottom' });
      return false;
    } finally {
      busy.value = false;
    }
  }

  async function saveChanges() {
    const current = chosen.value;
    if (!current) return;
    await write((list) => list.map((s) => (s.id === current.id ? { ...s, query: searchQuery(props.query) } : s)), `Saved ${current.name}`);
  }

  function startSaveAs() {
    draftName.value = chosen.value ? `${chosen.value.name} 2`.slice(0, MAX_MINE_SEARCH_NAME) : '';
    naming.value = true;
  }

  async function commitSaveAs() {
    if (problem.value) return;
    const name = draftName.value.trim();
    // The run settings carry over from the search this one started from.
    const next: MineSearch = { id: crypto.randomUUID(), name, query: searchQuery(props.query), run: chosen.value?.run ?? null };
    if (await write((list) => [...list, next], `Saved ${name}`)) {
      selected.value = next.id;
      naming.value = false;
    }
  }

  async function remove() {
    const current = chosen.value;
    if (!current) return;
    if (await write((list) => list.filter((s) => s.id !== current.id), `Deleted ${current.name}`)) {
      selected.value = null;
      deleting.value = false;
    }
  }
</script>

<template>
  <div class="flex flex-wrap items-center gap-2">
    <span class="text-sm font-medium">Saved search</span>
    <Select
      :model-value="selected"
      :options="options"
      option-label="name"
      option-value="id"
      placeholder="None"
      show-clear
      filter
      class="w-64 max-w-full"
      aria-label="Saved search"
      @update:model-value="choose"
    >
      <template #value="{ value, placeholder }">
        <span v-if="value && chosen">
          {{ chosen.name }}<span v-if="changed" class="text-gray-500 dark:text-gray-400"> (changed)</span>
        </span>
        <span v-else class="text-gray-500 dark:text-gray-400">{{ placeholder }}</span>
      </template>
    </Select>
    <Button
      v-if="chosen"
      v-tooltip.top="changed ? 'Save the current filters to this search' : 'No changes to save'"
      label="Save"
      icon="pi pi-save"
      size="small"
      severity="secondary"
      outlined
      :disabled="!changed || busy"
      @click="saveChanges"
    />
    <Button
      label="Save as…"
      size="small"
      severity="secondary"
      outlined
      :disabled="busy || searches.length >= MAX_MINE_SEARCHES"
      @click="startSaveAs"
    />
    <Button
      v-if="chosen"
      v-tooltip.top="'Delete this search'"
      icon="pi pi-trash"
      size="small"
      severity="danger"
      text
      rounded
      aria-label="Delete this search"
      :disabled="busy"
      @click="deleting = true"
    />

    <Dialog v-model:visible="naming" header="Save search" modal :style="{ width: '24rem' }" :breakpoints="{ '640px': '92vw' }">
      <form class="flex flex-col gap-2" @submit.prevent="commitSaveAs">
        <label class="flex flex-col gap-1">
          <span class="text-sm font-medium">Name</span>
          <InputText v-model="draftName" :maxlength="MAX_MINE_SEARCH_NAME" autofocus />
        </label>
        <small v-if="draftName && problem" class="text-red-600 dark:text-red-400">{{ problem }}</small>
        <div class="flex justify-end gap-2 mt-2">
          <Button label="Cancel" severity="secondary" text @click="naming = false" />
          <Button type="submit" label="Save" :disabled="!!problem" :loading="busy" />
        </div>
      </form>
    </Dialog>

    <Dialog v-model:visible="deleting" header="Delete search" modal :style="{ width: '24rem' }" :breakpoints="{ '640px': '92vw' }">
      <p>Delete <strong>{{ chosen?.name }}</strong>? The filters on the page stay as they are.</p>
      <div class="flex justify-end gap-2 mt-4">
        <Button label="Cancel" severity="secondary" text @click="deleting = false" />
        <Button label="Delete" severity="danger" :loading="busy" @click="remove" />
      </div>
    </Dialog>
  </div>
</template>
