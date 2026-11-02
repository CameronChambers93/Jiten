<script setup lang="ts">
  import { ankiConnect } from '~/composables/useAnkiConnect';
  import { fieldFor, type AnkiNoteTypeSetup } from '~/utils/ankiSetups';
  import { findSlots, handMadeSlots, insertRotation, installedVersion, removeRotation, SCRIPT_VERSION, type Slot } from '~/utils/ankiRotationScript';
  import { useToast } from 'primevue/usetoast';

  const props = defineProps<{ setup: AnkiNoteTypeSetup; fieldOrder: string[] }>();

  const anki = useAnkiSetups();
  const toast = useToast();

  /** Each card template's front and back, as Anki has them. */
  const templates = ref<Record<string, { Front: string; Back: string }> | null>(null);
  const error = ref<string | null>(null);
  const reviewing = ref(false);
  const working = ref(false);

  const listField = computed(() => fieldFor(props.setup.fieldRoles, 'sentenceList', props.fieldOrder.length ? props.fieldOrder : undefined));
  const listFieldMissing = computed(() => !!listField.value && props.fieldOrder.length > 0 && !props.fieldOrder.includes(listField.value));

  /** The version in Anki's templates: every side should agree; null when none has it. */
  const installed = computed(() => {
    if (!templates.value) return null;
    const versions = Object.values(templates.value).flatMap((t) => [installedVersion(t.Front), installedVersion(t.Back)]);
    return versions.some((v) => v != null) ? Math.min(...versions.map((v) => v ?? 0)) : null;
  });

  /** What inserting would do, per card template: the slots it wraps, or the template's own slots. */
  const plan = computed(() =>
    Object.entries(templates.value ?? {}).map(([name, t]) => {
      const own = [...new Set([...handMadeSlots(removeRotation(t.Front)), ...handMadeSlots(removeRotation(t.Back))])];
      const wrap = (side: string): Slot[] => findSlots(side, props.setup.fieldRoles);
      return { name, own, front: wrap(t.Front), back: wrap(t.Back) };
    })
  );

  async function loadTemplates() {
    error.value = null;
    try {
      templates.value = await ankiConnect<Record<string, { Front: string; Back: string }>>('modelTemplates', { modelName: props.setup.modelName });
    } catch (e) {
      error.value = extractApiError(e, "Couldn't read the templates");
    }
  }

  onMounted(loadTemplates);

  async function writeTemplates(next: Record<string, { Front: string; Back: string }>, version: number | null) {
    working.value = true;
    try {
      await ankiConnect('updateModelTemplates', { model: { name: props.setup.modelName, templates: next } });
      await anki.save({ ...structuredClone(toRaw(props.setup)), scriptVersion: version });
      toast.add({
        severity: 'success',
        summary: version ? `Rotation script v${version} is in ${props.setup.modelName}` : `Rotation script removed`,
        life: 3000,
      });
      reviewing.value = false;
      await loadTemplates();
    } catch (e) {
      toast.add({ severity: 'error', summary: "Couldn't change the templates", detail: extractApiError(e, 'AnkiConnect refused'), life: 6000 });
    } finally {
      working.value = false;
    }
  }

  function insert() {
    if (!templates.value || !listField.value) return;
    const next = Object.fromEntries(
      Object.entries(templates.value).map(([name, t]) => [
        name,
        {
          Front: insertRotation(t.Front, props.setup.fieldRoles, listField.value!, 'front'),
          Back: insertRotation(t.Back, props.setup.fieldRoles, listField.value!, 'back'),
        },
      ])
    );
    writeTemplates(next, SCRIPT_VERSION);
  }

  function remove() {
    if (!templates.value) return;
    const next = Object.fromEntries(
      Object.entries(templates.value).map(([name, t]) => [name, { Front: removeRotation(t.Front), Back: removeRotation(t.Back) }])
    );
    writeTemplates(next, null);
  }
</script>

<template>
  <div class="rounded border border-surface-200 p-3 dark:border-surface-700" data-testid="rotation-script">
    <div class="flex flex-wrap items-center justify-between gap-2">
      <div class="text-sm">
        <span class="font-semibold">Rotation script</span>
        <span class="text-gray-500 dark:text-gray-400">
          ·
          <template v-if="error">{{ error }}</template>
          <template v-else-if="!templates">reading the templates…</template>
          <template v-else-if="installed === SCRIPT_VERSION">v{{ installed }} is in the templates</template>
          <template v-else-if="installed">v{{ installed }} is in the templates; v{{ SCRIPT_VERSION }} is available</template>
          <template v-else>not in the templates yet</template>
        </span>
      </div>
      <div class="flex gap-2">
        <Button v-if="installed" label="Remove" size="small" severity="secondary" text :loading="working" @click="remove" />
        <Button
          :label="installed ? (installed === SCRIPT_VERSION ? 'Reinsert' : 'Update') : 'Insert into templates'"
          size="small"
          outlined
          :disabled="!templates || !listField || listFieldMissing"
          @click="reviewing = !reviewing"
        />
      </div>
    </div>
    <p class="mt-1 text-xs text-gray-500 dark:text-gray-400">
      Shows a different sentence of the word on every review, with a ⚑ Flag button on the back. Jiten adds it between
      <code>&lt;!-- jiten:rotation --&gt;</code> markers, so it can be updated or removed without touching the rest of the template.
    </p>
    <p v-if="listFieldMissing" class="mt-1 text-xs text-amber-700 dark:text-amber-400">
      The note type has no {{ listField }} field yet; Sync offers to add it.
    </p>

    <div v-if="reviewing" class="mt-3 flex flex-col gap-2 text-sm" data-testid="rotation-plan">
      <div v-for="t in plan" :key="t.name" class="rounded bg-surface-50 p-2 dark:bg-surface-800">
        <div class="font-semibold">{{ t.name }}</div>
        <div v-if="t.own.length">Uses the template's own slots: {{ t.own.join(', ') }}.</div>
        <template v-else>
          <div v-for="side in ['front', 'back'] as const" :key="side">
            <span class="text-gray-500 dark:text-gray-400">{{ side === 'front' ? 'Front' : 'Back' }}:</span>
            <template v-if="t[side].length">
              <span v-for="(s, i) in t[side]" :key="i"
                >{{ i ? ', ' : ' ' }}<code>{{ s.ref }}</code> → {{ s.slot }}</span
              >
            </template>
            <span v-else> no sentence fields; the block only adds its data<template v-if="side === 'back'"> and the flag line</template>.</span>
          </div>
        </template>
      </div>
      <p class="text-xs text-gray-500 dark:text-gray-400">
        Each slot shows the note's own field when it has no sentence list, and the picked sentence when it has one. Changing templates doesn't need a full sync.
      </p>
      <div class="flex justify-end gap-2">
        <Button label="Cancel" size="small" severity="secondary" text @click="reviewing = false" />
        <Button label="Insert" icon="pi pi-check" size="small" :loading="working" @click="insert" />
      </div>
    </div>
  </div>
</template>
