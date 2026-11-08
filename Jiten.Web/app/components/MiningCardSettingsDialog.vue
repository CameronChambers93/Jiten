<script setup lang="ts">
  import { ankiConnect } from '~/composables/useAnkiConnect';
  import {
    miningOf,
    newSetup,
    ROLE_OPTIONS,
    suggestRoles,
    withRoles,
    type EmptyFieldPolicies,
    type EmptyFieldPolicy,
    type FieldRole,
    type MiningSettings,
  } from '~/utils/ankiSetups';
  import { DEFAULT_EMPTY_FIELDS, UNWRITTEN_ROLES } from '~/utils/miningWarnings';

  const visible = defineModel<boolean>('visible', { required: true });
  const emit = defineEmits<{ saved: [] }>();

  const { $api } = useNuxtApp();
  const anki = useAnkiSetups();
  const { device, setKey } = useAnkiDevice();

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
  const ankiKey = ref('');
  const targetWordTemplate = ref('');
  const templateError = ref<string | null>(null);
  const saveError = ref<string | null>(null);

  const models = ref<string[]>([]);
  const loading = ref(false);
  const saving = ref(false);
  const ankiError = ref<string | null>(null);

  /** Note types to pick: those with sentence actions, then the rest of Anki's. */
  const typeOptions = computed(() => {
    const first = anki.miningSetups.value.map((s) => s.modelName).filter(Boolean);
    const seen = new Set<string>();
    return [...first, ...models.value].filter((m) => !seen.has(m) && !!seen.add(m));
  });

  const edit = computed(() => typeEdits.value[pickedType.value] ?? null);

  async function connect() {
    loading.value = true;
    ankiError.value = null;
    try {
      models.value = await ankiConnect<string[]>('modelNames', {}, ankiKey.value);
      models.value.sort();
      if (pickedType.value) await loadType(pickedType.value);
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

  watch(pickedType, (model) => {
    if (model && models.value.length) loadType(model).catch(() => {});
  });
  watch(
    visible,
    async (open) => {
      if (!open) return;
      await anki.load().catch(() => {});
      ankiKey.value = device.value.key;
      typeEdits.value = {};
      pickedType.value ||= anki.miningSetups.value[0]?.modelName ?? '';
      templateError.value = null;
      saveError.value = null;
      $api<{ targetWordTemplate: string }>('mined/settings').then((r) => (targetWordTemplate.value = r.targetWordTemplate));
      connect();
    },
    { immediate: true }
  );

  const templateValid = computed(() => targetWordTemplate.value.split('{word}').length === 2);

  const canSave = computed(() => templateValid.value);

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
      for (const [model, e] of Object.entries(typeEdits.value)) {
        const fieldRoles = Object.fromEntries(Object.entries(e.roles).filter(([, r]) => !!r)) as Record<string, FieldRole>;
        const setup = anki.setupFor(model);
        // A note type only looked at is left as it is.
        if (snapshot(e) === e.initial) continue;
        const base = setup ?? newSetup(model, e.fieldOrder);
        // Policies left at the defaults stay "the defaults", so a later change to them applies.
        const policies = JSON.stringify(e.mining.emptyFields) === JSON.stringify(DEFAULT_EMPTY_FIELDS) && !setup?.mining?.emptyFields ? null : { ...e.mining.emptyFields };
        await anki.save({ ...withRoles(base, fieldRoles), mining: { ...e.mining, parts: { ...e.mining.parts }, emptyFields: policies } });
      }
      setKey(ankiKey.value);
      visible.value = false;
      emit('saved');
    } catch (e) {
      saveError.value = extractApiError(e, "Couldn't save the card settings");
    } finally {
      saving.value = false;
    }
  }

  /** Whether the role can have an empty-field policy: roles only import reads can't. A part a pipeline doesn't build never counts as empty. */
  function canWarn(role: FieldRole | '' | undefined): role is FieldRole {
    return !!role && !UNWRITTEN_ROLES.includes(role);
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
        Mined sentences are added to Anki through AnkiConnect on this computer. These settings are saved to your account, except the API key. Where new cards go, what's
        built (audio, screenshot, video, English), tags and what happens when a word already has a card are set in
        <NuxtLink to="/settings/pipelines" class="underline" @click="visible = false">Pipelines</NuxtLink>.
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

      <div class="flex flex-col gap-4">
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
                    </div>
                  </div>
                </div>
              </div>
            </template>
            <p v-else-if="pickedType && loading" class="text-sm text-gray-500 dark:text-gray-400">Reading the note type from Anki…</p>
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
