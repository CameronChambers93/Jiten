<script setup lang="ts">
  import type { MiningFieldSource } from '~/utils/miningCardFields';
  import { MINING_FIELD_SOURCES } from '~/utils/miningCardFields';
  import { ankiConnect, useMiningCards } from '~/composables/useMiningCards';

  const visible = defineModel<boolean>('visible', { required: true });
  const emit = defineEmits<{ saved: [] }>();

  const { settings, saveSettings, mappingFor } = useMiningCards();

  const deckName = ref('');
  const modelName = ref('');
  const tags = ref('');
  const padStartMs = ref(250);
  const padEndMs = ref(250);
  const ankiKey = ref('');
  const fields = ref<Record<string, MiningFieldSource>>({});

  const decks = ref<string[]>([]);
  const models = ref<string[]>([]);
  const loading = ref(false);
  const ankiError = ref<string | null>(null);

  async function connect() {
    loading.value = true;
    ankiError.value = null;
    try {
      [decks.value, models.value] = await Promise.all([
        ankiConnect<string[]>('deckNames', {}, ankiKey.value),
        ankiConnect<string[]>('modelNames', {}, ankiKey.value),
      ]);
      decks.value.sort();
      models.value.sort();
      if (modelName.value) await loadFields(modelName.value);
    } catch (e) {
      ankiError.value = e instanceof TypeError ? 'unreachable' : String((e as Error).message ?? e);
    } finally {
      loading.value = false;
    }
  }

  async function loadFields(model: string) {
    const names = await ankiConnect<string[]>('modelFieldNames', { modelName: model }, ankiKey.value);
    fields.value = mappingFor(model, names);
  }

  watch(modelName, (model, previous) => {
    if (model && previous !== undefined && model !== previous && models.value.length) loadFields(model).catch(() => (fields.value = {}));
  });

  watch(
    visible,
    (open) => {
      if (!open) return;
      const s = settings.value;
      deckName.value = s.deckName;
      modelName.value = s.modelName;
      tags.value = s.tags;
      padStartMs.value = s.padStartMs;
      padEndMs.value = s.padEndMs;
      ankiKey.value = s.ankiKey;
      fields.value = { ...(s.fieldsByModel[s.modelName] ?? {}) };
      connect();
    },
    { immediate: true }
  );

  const fieldNames = computed(() => Object.keys(fields.value));
  const canSave = computed(() => !!deckName.value && !!modelName.value && fieldNames.value.length > 0);

  function save() {
    const s = settings.value;
    saveSettings({
      ...s,
      fieldsByModel: { ...s.fieldsByModel, [modelName.value]: { ...fields.value } },
      deckName: deckName.value,
      modelName: modelName.value,
      tags: tags.value,
      padStartMs: padStartMs.value,
      padEndMs: padEndMs.value,
      ankiKey: ankiKey.value,
    });
    visible.value = false;
    emit('saved');
  }

  const origin = import.meta.client ? window.location.origin : '';
</script>

<template>
  <Dialog v-model:visible="visible" modal header="Anki card settings" :style="{ width: '40rem', maxWidth: '95vw' }">
    <div class="flex flex-col gap-4">
      <p class="text-sm text-gray-600 dark:text-gray-300">
        Mined sentences are added to Anki through AnkiConnect on this computer, with the sentence audio and a screenshot cut from the episode.
      </p>

      <Message v-if="ankiError" severity="warn" :closable="false">
        <template v-if="ankiError === 'unreachable'">
          Couldn't reach AnkiConnect at 127.0.0.1:8765. Check that Anki is open and that <code>{{ origin }}</code> is in AnkiConnect's
          <code>webCorsOriginList</code>.
        </template>
        <template v-else>AnkiConnect: {{ ankiError }}</template>
        <div class="pt-2">
          <Button label="Try again" icon="pi pi-refresh" size="small" severity="secondary" outlined :loading="loading" @click="connect" />
        </div>
      </Message>

      <div class="grid grid-cols-1 sm:grid-cols-2 gap-3">
        <div class="flex flex-col gap-1">
          <label for="mining-deck" class="text-sm text-gray-500 dark:text-gray-400">Deck</label>
          <Select v-model="deckName" input-id="mining-deck" :options="decks" filter :loading="loading" placeholder="Choose a deck" />
        </div>
        <div class="flex flex-col gap-1">
          <label for="mining-model" class="text-sm text-gray-500 dark:text-gray-400">Note type</label>
          <Select v-model="modelName" input-id="mining-model" :options="models" filter :loading="loading" placeholder="Choose a note type" />
        </div>
      </div>

      <div v-if="fieldNames.length" class="flex flex-col gap-2">
        <div class="text-sm font-semibold">Fields</div>
        <div v-for="name in fieldNames" :key="name" class="grid grid-cols-[minmax(0,1fr)_minmax(0,1.4fr)] items-center gap-3">
          <label :for="`mining-field-${name}`" class="text-sm truncate">{{ name }}</label>
          <Select
            v-model="fields[name]"
            :input-id="`mining-field-${name}`"
            :options="MINING_FIELD_SOURCES"
            option-label="label"
            option-value="value"
            size="small"
          />
        </div>
      </div>

      <div class="grid grid-cols-1 sm:grid-cols-3 gap-3">
        <div class="flex flex-col gap-1">
          <label for="mining-tags" class="text-sm text-gray-500 dark:text-gray-400">Tags</label>
          <InputText id="mining-tags" v-model="tags" />
        </div>
        <div class="flex flex-col gap-1">
          <label for="mining-pad-start" class="text-sm text-gray-500 dark:text-gray-400">Audio lead-in (ms)</label>
          <InputNumber v-model="padStartMs" input-id="mining-pad-start" :min="0" :max="5000" :step="50" show-buttons />
        </div>
        <div class="flex flex-col gap-1">
          <label for="mining-pad-end" class="text-sm text-gray-500 dark:text-gray-400">Audio tail (ms)</label>
          <InputNumber v-model="padEndMs" input-id="mining-pad-end" :min="0" :max="5000" :step="50" show-buttons />
        </div>
      </div>

      <div class="flex flex-col gap-1">
        <label for="mining-key" class="text-sm text-gray-500 dark:text-gray-400">AnkiConnect API key (only if you set one)</label>
        <InputText id="mining-key" v-model="ankiKey" type="password" autocomplete="off" />
      </div>

      <div class="flex justify-end gap-2 pt-2">
        <Button label="Cancel" severity="secondary" @click="visible = false" />
        <Button label="Save" icon="pi pi-check" :disabled="!canSave" @click="save" />
      </div>
    </div>
  </Dialog>
</template>
