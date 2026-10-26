<script setup lang="ts">
  import Card from 'primevue/card';
  import Button from 'primevue/button';
  import { useToast } from 'primevue/usetoast';
  import { useConfirm } from 'primevue/useconfirm';

  useHead({ title: 'Frequency dictionaries - Jiten' });

  definePageMeta({
    middleware: ['auth-admin'],
  });

  interface FrequencyDictionary {
    id: number;
    title: string;
    revision: string | null;
    importedAt: string;
    entryCount: number;
    rankedForms: number;
  }

  const { $api } = useNuxtApp();
  const toast = useToast();
  const confirm = useConfirm();

  const dictionaries = ref<FrequencyDictionary[]>([]);
  const loading = ref(true);
  const uploading = ref(false);
  const fileInput = ref<HTMLInputElement | null>(null);

  async function load() {
    loading.value = true;
    try {
      dictionaries.value = await $api<FrequencyDictionary[]>('admin/frequency-dictionaries');
    } finally {
      loading.value = false;
    }
  }

  async function upload(event: Event) {
    const file = (event.target as HTMLInputElement).files?.[0];
    if (!file) return;
    uploading.value = true;
    try {
      const body = new FormData();
      body.append('file', file);
      const result = await $api<FrequencyDictionary>('admin/frequency-dictionaries', { method: 'POST', body });
      toast.add({ severity: 'success', summary: `Imported ${result.title}`, detail: `${result.rankedForms.toLocaleString()} word forms ranked`, life: 4000 });
      await load();
    } catch (e) {
      const message = (e as { data?: { message?: string } })?.data?.message;
      toast.add({ severity: 'error', summary: message ?? "Couldn't import the dictionary", life: 6000 });
    } finally {
      uploading.value = false;
      if (fileInput.value) fileInput.value.value = '';
    }
  }

  function remove(dictionary: FrequencyDictionary) {
    confirm.require({
      message: `Delete ${dictionary.title}?`,
      header: 'Delete dictionary',
      acceptProps: { label: 'Delete', severity: 'danger' },
      rejectProps: { label: 'Cancel', severity: 'secondary', outlined: true },
      accept: async () => {
        await $api(`admin/frequency-dictionaries/${dictionary.id}`, { method: 'DELETE' });
        await load();
      },
    });
  }

  onMounted(load);
</script>

<template>
  <div class="container mx-auto p-4 flex flex-col gap-4 max-w-3xl">
    <h1 class="text-2xl font-bold">Frequency dictionaries</h1>
    <p class="text-gray-600 dark:text-gray-300">
      Yomitan frequency dictionaries (the .zip you'd import into Yomitan). The Mine page sorts and filters target words by their rank. Importing a
      dictionary with the same title replaces it.
    </p>

    <Card>
      <template #content>
        <div class="flex flex-col gap-3">
          <div class="flex items-center gap-3">
            <input ref="fileInput" type="file" accept=".zip" class="hidden" @change="upload" />
            <Button label="Import a dictionary" icon="pi pi-upload" :loading="uploading" @click="fileInput?.click()" />
            <span v-if="uploading" class="text-sm text-gray-500">Ranking every word form, this takes a few seconds…</span>
          </div>

          <p v-if="loading" class="text-gray-500">Loading…</p>
          <p v-else-if="dictionaries.length === 0" class="text-gray-500">No dictionaries yet.</p>
          <ul v-else class="flex flex-col divide-y divide-surface-200 dark:divide-surface-700">
            <li v-for="d in dictionaries" :key="d.id" class="py-3 flex items-center justify-between gap-3">
              <div>
                <div class="font-semibold">{{ d.title }}</div>
                <div class="text-sm text-gray-500 dark:text-gray-400">
                  {{ d.revision }} · {{ d.entryCount.toLocaleString() }} entries · {{ d.rankedForms.toLocaleString() }} word forms ranked · imported
                  {{ new Date(d.importedAt).toLocaleDateString() }}
                </div>
              </div>
              <Button icon="pi pi-trash" severity="danger" text rounded aria-label="Delete" @click="remove(d)" />
            </li>
          </ul>
        </div>
      </template>
    </Card>
  </div>
</template>
