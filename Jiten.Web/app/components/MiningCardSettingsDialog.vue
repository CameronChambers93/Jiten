<script setup lang="ts">
  import { ankiConnect } from '~/composables/useAnkiConnect';
  import { ankiSearchValue, hasSentenceActions, newSetup, ROLE_OPTIONS, setupQuery, withRoles, type FieldRole } from '~/utils/ankiSetups';

  const visible = defineModel<boolean>('visible', { required: true });
  const emit = defineEmits<{ saved: [] }>();

  const { $api } = useNuxtApp();
  const anki = useAnkiSetups();
  const { device, setKey } = useAnkiDevice();

  const deckName = ref('');
  const modelName = ref('');
  const tags = ref('');
  const padStartMs = ref(250);
  const padEndMs = ref(250);
  const ankiKey = ref('');
  const targetWordTemplate = ref('');
  const templateError = ref<string | null>(null);
  const saveError = ref<string | null>(null);
  /** The note type's fields in Anki's order, with their roles. */
  const fieldOrder = ref<string[]>([]);
  const roles = ref<Record<string, FieldRole | ''>>({});
  /** Set when new cards of this note type and deck won't be synced. */
  const syncWarning = ref<string | null>(null);

  const decks = ref<string[]>([]);
  const models = ref<string[]>([]);
  const loading = ref(false);
  const saving = ref(false);
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

  /** The note type's fields, with its setup's roles or, without a setup, suggested ones. */
  async function loadFields(model: string) {
    const names = await ankiConnect<string[]>('modelFieldNames', { modelName: model }, ankiKey.value);
    fieldOrder.value = names;
    const setup = anki.setupFor(model);
    const source = setup?.fieldRoles ?? newSetup(model, names).fieldRoles;
    roles.value = Object.fromEntries(names.map((n) => [n, source[n] ?? '']));
    await checkSync();
  }

  /** Warns when the note type isn't synced, or its filter leaves out the deck. */
  async function checkSync() {
    syncWarning.value = null;
    const setup = anki.setupFor(modelName.value);
    if (!modelName.value) return;
    if (!setup || !hasSentenceActions(setup)) {
      syncWarning.value = `${modelName.value} has no sentence actions in Anki settings, so new cards won't sync.`;
      return;
    }
    if (!setup.filter || !deckName.value) return;
    try {
      const deck = `"deck:${ankiSearchValue(deckName.value)}"`;
      const [all, kept] = await Promise.all([
        ankiConnect<number[]>('findNotes', { query: setupQuery({ modelName: modelName.value, filter: null }, deck) }, ankiKey.value),
        ankiConnect<number[]>('findNotes', { query: setupQuery(setup, deck) }, ankiKey.value),
      ]);
      if (all.length > 0 && kept.length === 0) syncWarning.value = `The ${modelName.value} filter leaves out ${deckName.value}, so new cards won't sync.`;
    } catch {}
  }

  watch(modelName, (model, previous) => {
    if (model && previous !== undefined && model !== previous && models.value.length) loadFields(model).catch(() => (roles.value = {}));
  });
  watch(deckName, () => checkSync());

  watch(
    visible,
    async (open) => {
      if (!open) return;
      await anki.load().catch(() => {});
      const t = anki.cardTarget.value;
      deckName.value = t.deckName;
      modelName.value = t.modelName;
      tags.value = t.tags;
      padStartMs.value = t.padStartMs;
      padEndMs.value = t.padEndMs;
      ankiKey.value = device.value.key;
      roles.value = {};
      fieldOrder.value = [];
      templateError.value = null;
      saveError.value = null;
      $api<{ targetWordTemplate: string }>('mined/settings').then((r) => (targetWordTemplate.value = r.targetWordTemplate));
      connect();
    },
    { immediate: true }
  );

  const canSave = computed(() => !!deckName.value && !!modelName.value && fieldOrder.value.length > 0 && templateValid.value);

  const templateValid = computed(() => targetWordTemplate.value.split('{word}').length === 2);

  async function save() {
    saving.value = true;
    saveError.value = null;
    try {
      try {
        await $api('mined/settings', { method: 'PUT', body: { targetWordTemplate: targetWordTemplate.value } });
      } catch (e) {
        templateError.value = String((e as Error).message ?? e);
        return;
      }
      const fieldRoles = Object.fromEntries(Object.entries(roles.value).filter(([, r]) => !!r)) as Record<string, FieldRole>;
      const setup = anki.setupFor(modelName.value) ?? newSetup(modelName.value, fieldOrder.value);
      await anki.save(withRoles(setup, fieldRoles));
      await anki.saveCardTarget({
        deckName: deckName.value,
        modelName: modelName.value,
        tags: tags.value,
        padStartMs: padStartMs.value,
        padEndMs: padEndMs.value,
      });
      setKey(ankiKey.value);
      visible.value = false;
      emit('saved');
    } catch (e) {
      saveError.value = extractApiError(e, "Couldn't save the card settings");
    } finally {
      saving.value = false;
    }
  }

  const origin = import.meta.client ? window.location.origin : '';
</script>

<template>
  <Dialog v-model:visible="visible" modal header="Anki card settings" :style="{ width: '40rem', maxWidth: '95vw' }">
    <div class="flex flex-col gap-4">
      <p class="text-sm text-gray-600 dark:text-gray-300">
        Mined sentences are added to Anki through AnkiConnect on this computer, with the sentence audio and a screenshot cut from the episode. These settings
        are saved to your account, except the API key.
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

      <Message v-if="syncWarning" severity="warn" size="small" :closable="false">
        {{ syncWarning }} <NuxtLink to="/settings/anki" class="underline" @click="visible = false">Anki settings</NuxtLink>
      </Message>

      <div v-if="fieldOrder.length" class="flex flex-col gap-2">
        <div class="text-sm font-semibold">Fields</div>
        <p class="text-xs text-gray-500 dark:text-gray-400">These are the note type's field roles, which import and Sync read too.</p>
        <div v-for="name in fieldOrder" :key="name" class="grid grid-cols-[minmax(0,1fr)_minmax(0,1.4fr)] items-center gap-3">
          <label :for="`mining-field-${name}`" class="text-sm truncate">{{ name }}</label>
          <Select v-model="roles[name]" :input-id="`mining-field-${name}`" :options="ROLE_OPTIONS" option-label="label" option-value="value" size="small" />
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
        <label for="mining-target" class="text-sm text-gray-500 dark:text-gray-400">Target word markup</label>
        <InputText id="mining-target" v-model="targetWordTemplate" :invalid="!templateValid" spellcheck="false" />
        <small class="text-gray-500 dark:text-gray-400">
          HTML around the word, with <code>{word}</code> once. Lapis+ colours the word's pitch only inside <code>&lt;b&gt;</code>. Sync rewrites the Sentences
          field with this; main fields keep what they were written with.
        </small>
        <small v-if="templateError" class="text-red-500">{{ templateError }}</small>
      </div>

      <div class="flex flex-col gap-1">
        <label for="mining-key" class="text-sm text-gray-500 dark:text-gray-400">AnkiConnect API key on this device (only if you set one)</label>
        <InputText id="mining-key" v-model="ankiKey" type="password" autocomplete="off" />
      </div>

      <small v-if="saveError" class="text-red-500">{{ saveError }}</small>

      <div class="flex justify-end gap-2 pt-2">
        <Button label="Cancel" severity="secondary" @click="visible = false" />
        <Button label="Save" icon="pi pi-check" :disabled="!canSave" :loading="saving" @click="save" />
      </div>
    </div>
  </Dialog>
</template>
