<script setup lang="ts">
  import type { DeepReadonly } from 'vue';
  import { ankiConnect } from '~/composables/useAnkiConnect';
  import { MediaType } from '~/types/enums';
  import type { MediaSuggestion } from '~/types/types';
  import { getMediaTypeText } from '~/utils/mediaTypeMapper';
  import {
    ankiSearchValue,
    DEFAULT_PARTS,
    hasSentenceActions,
    miningOf,
    newSetup,
    partsOffRoles,
    ROLE_OPTIONS,
    setupQuery,
    suggestRoles,
    withRoles,
    type EmptyFieldPolicies,
    type EmptyFieldPolicy,
    type FieldRole,
    type MiningSettings,
    type NewCardTarget,
  } from '~/utils/ankiSetups';
  import { DEFAULT_EMPTY_FIELDS, UNWRITTEN_ROLES } from '~/utils/miningWarnings';

  const visible = defineModel<boolean>('visible', { required: true });
  const emit = defineEmits<{ saved: [] }>();

  const { $api } = useNuxtApp();
  const anki = useAnkiSetups();
  const { device, setKey } = useAnkiDevice();
  const localiseTitle = useLocaliseTitle();

  const tab = ref<'new' | 'types'>('new');

  /** A target being edited. The last one's rule is dropped on save, since it takes everything else. */
  interface TargetEdit {
    key: number;
    deckName: string;
    modelName: string;
    tags: string;
    mediaTypes: number[];
    titles: { deckId: number; title: string }[];
  }
  let nextKey = 0;
  const targets = ref<TargetEdit[]>([]);
  const padStartMs = ref(250);
  const padEndMs = ref(250);
  /** Per target key: why its cards won't sync. */
  const syncWarnings = ref<Record<number, string>>({});

  /** A note type being edited: its fields in Anki's order, roles, and mining settings. */
  interface TypeEdit {
    fieldOrder: string[];
    roles: Record<string, FieldRole | ''>;
    mining: MiningSettings;
    /** The preset whose fields the note type has, when its roles don't use it yet. */
    preset: { name: string; roles: Record<string, FieldRole> } | null;
    /** The roles and mining settings as loaded, so a note type only looked at isn't saved. */
    initial: string;
  }

  function snapshot(e: Pick<TypeEdit, 'roles' | 'mining'>): string {
    return JSON.stringify([e.roles, e.mining]);
  }
  const typeEdits = ref<Record<string, TypeEdit>>({});
  const pickedType = ref('');

  const POLICY_OPTIONS: { value: EmptyFieldPolicy | 'off'; label: string }[] = [
    { value: 'off', label: 'Off' },
    { value: 'warn', label: 'Warn' },
    { value: 'deny', label: 'Deny' },
  ];
  const EXISTING_OPTIONS = [
    { value: 'auto', label: 'Add to it automatically' },
    { value: 'ask', label: 'Ask' },
  ];
  const PART_OPTIONS: { key: keyof MiningSettings['parts']; label: string }[] = [
    { key: 'audio', label: 'Audio' },
    { key: 'picture', label: 'Screenshot' },
    { key: 'english', label: 'English' },
  ];
  const MEDIA_TYPE_OPTIONS = (Object.values(MediaType).filter((v) => typeof v === 'number') as MediaType[]).map((value) => ({
    value,
    label: getMediaTypeText(value),
  }));

  const ankiKey = ref('');
  const targetWordTemplate = ref('');
  const templateError = ref<string | null>(null);
  const saveError = ref<string | null>(null);

  const decks = ref<string[]>([]);
  const models = ref<string[]>([]);
  const loading = ref(false);
  const saving = ref(false);
  const ankiError = ref<string | null>(null);

  /** Note types for the Note types tab: those with sentence actions, then the targets', then the rest of Anki's. */
  const typeOptions = computed(() => {
    const first = [...anki.miningSetups.value.map((s) => s.modelName), ...targets.value.map((t) => t.modelName)].filter(Boolean);
    const seen = new Set<string>();
    return [...first, ...models.value].filter((m) => !seen.has(m) && !!seen.add(m));
  });

  const edit = computed(() => typeEdits.value[pickedType.value] ?? null);

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
      if (pickedType.value) await loadType(pickedType.value);
      await checkSync();
    } catch (e) {
      ankiError.value = e instanceof TypeError ? 'unreachable' : String((e as Error).message ?? e);
    } finally {
      loading.value = false;
    }
  }

  /** The note type's fields, with its setup's roles or, without a setup, suggested ones; read once per opening. */
  async function loadType(model: string) {
    if (!model || typeEdits.value[model]) return;
    const names = await ankiConnect<string[]>('modelFieldNames', { modelName: model }, ankiKey.value);
    const setup = anki.setupFor(model);
    const source = setup?.fieldRoles ?? newSetup(model, names).fieldRoles;
    const suggested = suggestRoles(model, names);
    const presetUnused = !!suggested.preset && Object.entries(suggested.roles).some(([f, r]) => source[f] !== r);
    const mining = miningOf(setup);
    const roles = Object.fromEntries(names.map((n) => [n, source[n] ?? '']));
    const m = { ...mining, parts: { ...mining.parts }, emptyFields: { ...(mining.emptyFields ?? DEFAULT_EMPTY_FIELDS) } };
    typeEdits.value = {
      ...typeEdits.value,
      [model]: {
        fieldOrder: names,
        roles,
        mining: m,
        preset: presetUnused ? { name: suggested.preset!, roles: suggested.roles } : null,
        initial: snapshot({ roles, mining: m }),
      },
    };
  }

  function usePreset() {
    const e = edit.value;
    if (!e?.preset) return;
    const roles = e.preset.roles;
    e.roles = Object.fromEntries(e.fieldOrder.map((n) => [n, roles[n] ?? '']));
    e.preset = null;
  }

  /** Warns per target when its note type isn't synced, or its filter leaves out the deck. */
  async function checkSync() {
    const warnings: Record<number, string> = {};
    for (const t of targets.value) {
      if (!t.modelName) continue;
      const setup = anki.setupFor(t.modelName);
      if (!setup || !hasSentenceActions(setup)) {
        warnings[t.key] = `${t.modelName} has no sentence actions in Anki settings, so new cards won't sync.`;
        continue;
      }
      if (!setup.filter || !t.deckName) continue;
      try {
        const deck = `"deck:${ankiSearchValue(t.deckName)}"`;
        const [all, kept] = await Promise.all([
          ankiConnect<number[]>('findNotes', { query: setupQuery({ modelName: t.modelName, filter: null }, deck) }, ankiKey.value),
          ankiConnect<number[]>('findNotes', { query: setupQuery(setup, deck) }, ankiKey.value),
        ]);
        if (all.length > 0 && kept.length === 0) warnings[t.key] = `The ${t.modelName} filter leaves out ${t.deckName}, so new cards won't sync.`;
      } catch {}
    }
    syncWarnings.value = warnings;
  }

  watch(pickedType, (model) => {
    if (model && models.value.length) loadType(model).catch(() => {});
  });
  watch(
    () => targets.value.map((t) => `${t.deckName}\u0000${t.modelName}`).join('\u0001'),
    () => {
      if (models.value.length) checkSync();
    }
  );

  function toEdit(t: DeepReadonly<NewCardTarget>): TargetEdit {
    return {
      key: nextKey++,
      deckName: t.deckName,
      modelName: t.modelName,
      tags: t.tags,
      mediaTypes: [...(t.when?.mediaTypes ?? [])],
      titles: (t.when?.titles ?? []).map((x) => ({ ...x })),
    };
  }

  watch(
    visible,
    async (open) => {
      if (!open) return;
      await anki.load().catch(() => {});
      const t = anki.cardTargets.value;
      targets.value = t.targets.map(toEdit);
      padStartMs.value = t.padStartMs;
      padEndMs.value = t.padEndMs;
      ankiKey.value = device.value.key;
      typeEdits.value = {};
      syncWarnings.value = {};
      pickedType.value = t.targets[t.targets.length - 1]?.modelName ?? '';
      templateError.value = null;
      saveError.value = null;
      tab.value = 'new';
      $api<{ targetWordTemplate: string }>('mined/settings').then((r) => (targetWordTemplate.value = r.targetWordTemplate));
      connect();
    },
    { immediate: true }
  );

  function addTarget() {
    // A new target goes before the last, which takes everything else.
    const last = targets.value[targets.value.length - 1];
    targets.value.splice(Math.max(0, targets.value.length - 1), 0, {
      key: nextKey++,
      deckName: last?.deckName ?? '',
      modelName: last?.modelName ?? '',
      tags: last?.tags ?? 'jiten',
      mediaTypes: [],
      titles: [],
    });
  }

  function removeTarget(i: number) {
    targets.value.splice(i, 1);
  }

  function moveTarget(i: number, by: -1 | 1) {
    const j = i + by;
    if (j < 0 || j >= targets.value.length) return;
    const list = [...targets.value];
    [list[i], list[j]] = [list[j]!, list[i]!];
    targets.value = list;
  }

  /** The title picker's value per target; cleared after each pick. */
  const titlePick = ref<Record<number, number | null>>({});

  function addTitle(t: TargetEdit, suggestion: MediaSuggestion | null) {
    if (!suggestion) return;
    if (!t.titles.some((x) => x.deckId === suggestion.deckId)) t.titles.push({ deckId: suggestion.deckId, title: localiseTitle(suggestion) });
    nextTick(() => (titlePick.value[t.key] = null));
  }

  function ruleEmpty(t: TargetEdit) {
    return t.mediaTypes.length === 0 && t.titles.length === 0;
  }

  const templateValid = computed(() => targetWordTemplate.value.split('{word}').length === 2);

  const canSave = computed(
    () =>
      targets.value.length > 0 &&
      targets.value.every((t) => !!t.deckName && !!t.modelName) &&
      targets.value.slice(0, -1).every((t) => !ruleEmpty(t)) &&
      templateValid.value
  );

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
      // Every target's note type needs a setup with roles; one without gets its preset or suggested roles.
      for (const model of new Set(targets.value.map((t) => t.modelName))) {
        if (!typeEdits.value[model] && !anki.setupFor(model)) await loadType(model);
      }
      for (const [model, e] of Object.entries(typeEdits.value)) {
        const fieldRoles = Object.fromEntries(Object.entries(e.roles).filter(([, r]) => !!r)) as Record<string, FieldRole>;
        const setup = anki.setupFor(model);
        const changed = snapshot(e) !== e.initial;
        // A note type only looked at is left as it is; a target's note type without a setup gets one.
        if (setup ? !changed : !targets.value.some((t) => t.modelName === model)) continue;
        const base = setup ?? newSetup(model, e.fieldOrder);
        // Policies left at the defaults stay "the defaults", so a later change to them applies.
        const policies = JSON.stringify(e.mining.emptyFields) === JSON.stringify(DEFAULT_EMPTY_FIELDS) && !setup?.mining?.emptyFields ? null : { ...e.mining.emptyFields };
        await anki.save({ ...withRoles(base, fieldRoles), mining: { ...e.mining, parts: { ...e.mining.parts }, emptyFields: policies } });
      }
      await anki.saveCardTargets({
        targets: targets.value.map((t, i, all) => ({
          deckName: t.deckName,
          modelName: t.modelName,
          tags: t.tags,
          when: i === all.length - 1 ? null : { mediaTypes: [...t.mediaTypes], titles: t.titles.map((x) => ({ ...x })) },
        })),
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

  function partOff(role: FieldRole | '' | undefined): boolean {
    return !!role && partsOffRoles(edit.value?.mining.parts ?? DEFAULT_PARTS).includes(role);
  }

  /** Whether the role can have an empty-field policy: roles only import reads can't, nor parts the note type turns off. */
  function canWarn(role: FieldRole | '' | undefined): role is FieldRole {
    return !!role && !UNWRITTEN_ROLES.includes(role) && !partOff(role);
  }

  function policyOf(role: FieldRole): EmptyFieldPolicy | 'off' {
    return edit.value?.mining.emptyFields?.[role] ?? 'off';
  }

  function setPolicy(role: FieldRole, policy: EmptyFieldPolicy | 'off') {
    const e = edit.value;
    if (!e) return;
    const rest = Object.entries(e.mining.emptyFields ?? {}).filter(([r]) => r !== role);
    e.mining.emptyFields = Object.fromEntries(policy === 'off' ? rest : [...rest, [role, policy]]) as EmptyFieldPolicies;
  }

  const origin = import.meta.client ? window.location.origin : '';
</script>

<template>
  <Dialog v-model:visible="visible" modal header="Anki card settings" :style="{ width: '44rem', maxWidth: '95vw' }">
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

      <Tabs v-model:value="tab" :show-navigators="false">
        <TabList>
          <Tab value="new">New cards</Tab>
          <Tab value="types">Note types</Tab>
        </TabList>
        <TabPanels class="!px-0">
          <TabPanel value="new" class="flex flex-col gap-3">
            <p class="text-xs text-gray-500 dark:text-gray-400">A new card goes to the first target whose rule matches the line. The last one takes everything else.</p>

            <div v-for="(t, i) in targets" :key="t.key" class="flex flex-col gap-3 rounded-lg border border-gray-200 dark:border-gray-700 p-3" data-testid="card-target">
              <div class="flex items-center justify-between gap-2 flex-wrap">
                <span class="text-sm font-semibold">
                  {{ i + 1 }}. {{ i < targets.length - 1 ? 'When the line is from' : targets.length > 1 ? 'Everything else' : 'Every line' }}
                </span>
                <div class="flex gap-1">
                  <Button icon="pi pi-arrow-up" size="small" severity="secondary" text :disabled="i === 0" aria-label="Move up" @click="moveTarget(i, -1)" />
                  <Button
                    icon="pi pi-arrow-down"
                    size="small"
                    severity="secondary"
                    text
                    :disabled="i === targets.length - 1"
                    aria-label="Move down"
                    @click="moveTarget(i, 1)"
                  />
                  <Button icon="pi pi-trash" size="small" severity="secondary" text :disabled="targets.length === 1" aria-label="Remove" @click="removeTarget(i)" />
                </div>
              </div>

              <div v-if="i < targets.length - 1" class="flex flex-col gap-2">
                <div class="grid grid-cols-1 sm:grid-cols-2 gap-3">
                  <div class="flex flex-col gap-1 min-w-0">
                    <label :for="`target-media-${t.key}`" class="text-xs text-gray-500 dark:text-gray-400">Media types</label>
                    <MultiSelect
                      v-model="t.mediaTypes"
                      :input-id="`target-media-${t.key}`"
                      :options="MEDIA_TYPE_OPTIONS"
                      option-label="label"
                      option-value="value"
                      display="chip"
                      placeholder="None"
                      size="small"
                      class="w-full"
                    />
                  </div>
                  <div class="flex flex-col gap-1 min-w-0">
                    <label :for="`target-title-${t.key}`" class="text-xs text-gray-500 dark:text-gray-400">or titles</label>
                    <MediaDeckPicker
                      v-model="titlePick[t.key]"
                      :input-id="`target-title-${t.key}`"
                      :allow-raw-id="false"
                      placeholder="Search for a title..."
                      @select="(s: MediaSuggestion | null) => addTitle(t, s)"
                    />
                  </div>
                </div>
                <div v-if="t.titles.length" class="flex flex-wrap gap-2">
                  <Chip v-for="(title, j) in t.titles" :key="title.deckId" :label="title.title" removable @remove="t.titles.splice(j, 1)" />
                </div>
                <small v-if="ruleEmpty(t)" class="text-red-500">Choose a media type or a title; only the last target takes everything else.</small>
              </div>

              <div class="grid grid-cols-1 sm:grid-cols-3 gap-3">
                <div class="flex flex-col gap-1 min-w-0">
                  <label :for="`target-deck-${t.key}`" class="text-xs text-gray-500 dark:text-gray-400">Deck</label>
                  <Select v-model="t.deckName" :input-id="`target-deck-${t.key}`" :options="decks" filter :loading="loading" placeholder="Choose a deck" size="small" />
                </div>
                <div class="flex flex-col gap-1 min-w-0">
                  <label :for="`target-model-${t.key}`" class="text-xs text-gray-500 dark:text-gray-400">Note type</label>
                  <Select
                    v-model="t.modelName"
                    :input-id="`target-model-${t.key}`"
                    :options="models"
                    filter
                    :loading="loading"
                    placeholder="Choose a note type"
                    size="small"
                  />
                </div>
                <div class="flex flex-col gap-1 min-w-0">
                  <label :for="`target-tags-${t.key}`" class="text-xs text-gray-500 dark:text-gray-400">Tags</label>
                  <InputText :id="`target-tags-${t.key}`" v-model="t.tags" size="small" />
                </div>
              </div>

              <Message v-if="syncWarnings[t.key]" severity="warn" size="small" :closable="false">
                {{ syncWarnings[t.key] }} <NuxtLink to="/settings/anki" class="underline" @click="visible = false">Anki settings</NuxtLink>
              </Message>
            </div>

            <div>
              <Button label="Add a target" icon="pi pi-plus" size="small" severity="secondary" outlined @click="addTarget" />
            </div>

            <div class="grid grid-cols-1 sm:grid-cols-2 gap-3">
              <div class="flex flex-col gap-1 min-w-0">
                <label for="mining-pad-start" class="text-sm text-gray-500 dark:text-gray-400">Audio lead-in (ms)</label>
                <InputNumber v-model="padStartMs" input-id="mining-pad-start" :min="0" :max="5000" :step="50" show-buttons fluid />
              </div>
              <div class="flex flex-col gap-1 min-w-0">
                <label for="mining-pad-end" class="text-sm text-gray-500 dark:text-gray-400">Audio tail (ms)</label>
                <InputNumber v-model="padEndMs" input-id="mining-pad-end" :min="0" :max="5000" :step="50" show-buttons fluid />
              </div>
            </div>
          </TabPanel>

          <TabPanel value="types" class="flex flex-col gap-4">
            <div class="flex flex-col gap-1 sm:max-w-80">
              <label for="mining-type" class="text-sm text-gray-500 dark:text-gray-400">Note type</label>
              <Select v-model="pickedType" input-id="mining-type" :options="typeOptions" filter :loading="loading" placeholder="Choose a note type" />
            </div>

            <template v-if="edit">
              <Message v-if="edit.preset" severity="info" size="small" :closable="false">
                <div class="flex flex-wrap items-center gap-2">
                  <span>{{ pickedType }} has the {{ edit.preset.name }} preset's fields, but its roles don't use them all.</span>
                  <Button label="Use the preset" size="small" severity="info" outlined @click="usePreset" />
                </div>
              </Message>

              <div class="flex flex-col gap-1">
                <span class="text-sm text-gray-500 dark:text-gray-400">When a word already has a {{ pickedType }} card</span>
                <SelectButton
                  v-model="edit.mining.existing"
                  :options="EXISTING_OPTIONS"
                  option-label="label"
                  option-value="value"
                  :allow-empty="false"
                  size="small"
                  aria-label="When a word already has a card"
                />
              </div>

              <div class="flex flex-col gap-1">
                <span class="text-sm text-gray-500 dark:text-gray-400">Mining builds</span>
                <div class="flex flex-wrap gap-4">
                  <div v-for="p in PART_OPTIONS" :key="p.key" class="flex items-center gap-2">
                    <Checkbox v-model="edit.mining.parts[p.key]" :input-id="`mining-part-${p.key}`" binary />
                    <label :for="`mining-part-${p.key}`" class="text-sm">{{ p.label }}</label>
                  </div>
                </div>
                <small class="text-xs text-gray-500 dark:text-gray-400">A part that's off isn't cut, isn't stored with the sentence, and never counts as empty.</small>
              </div>

              <div class="flex flex-col gap-2">
                <div class="text-sm font-semibold">Fields</div>
                <p class="text-xs text-gray-500 dark:text-gray-400">
                  These are the note type's field roles, which import and Sync read too. The last column says what mining does when it would leave the field empty:
                  Warn adds the card and tells you; Deny doesn't add it.
                </p>
                <div class="overflow-x-auto">
                  <div class="flex flex-col gap-2 min-w-[24rem]">
                    <div
                      v-for="name in edit.fieldOrder"
                      :key="name"
                      class="grid grid-cols-[minmax(0,1fr)_minmax(0,1.4fr)_6rem] items-center gap-3"
                      :class="{ 'opacity-60': partOff(edit.roles[name]) }"
                    >
                      <label :for="`mining-field-${name}`" class="text-sm truncate" :title="name">{{ name }}</label>
                      <Select
                        v-model="edit.roles[name]"
                        :input-id="`mining-field-${name}`"
                        :options="ROLE_OPTIONS"
                        option-label="label"
                        option-value="value"
                        size="small"
                      />
                      <Select
                        v-if="canWarn(edit.roles[name])"
                        :model-value="policyOf(edit.roles[name] as FieldRole)"
                        :options="POLICY_OPTIONS"
                        option-label="label"
                        option-value="value"
                        size="small"
                        :input-id="`mining-empty-${name}`"
                        :aria-label="`When ${name} would be empty`"
                        @update:model-value="(p: EmptyFieldPolicy | 'off') => setPolicy(edit!.roles[name] as FieldRole, p)"
                      />
                      <span v-else-if="partOff(edit.roles[name])" class="text-xs text-gray-500 dark:text-gray-400">Not built</span>
                    </div>
                  </div>
                </div>
              </div>
            </template>
            <p v-else-if="pickedType && loading" class="text-sm text-gray-500 dark:text-gray-400">Reading the note type from Anki…</p>
          </TabPanel>
        </TabPanels>
      </Tabs>

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
