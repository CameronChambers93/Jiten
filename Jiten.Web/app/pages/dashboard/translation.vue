<script setup lang="ts">
  import Card from 'primevue/card';
  import Button from 'primevue/button';
  import InputText from 'primevue/inputtext';
  import InputNumber from 'primevue/inputnumber';
  import Password from 'primevue/password';
  import Select from 'primevue/select';
  import SelectButton from 'primevue/selectbutton';
  import Textarea from 'primevue/textarea';
  import ToggleSwitch from 'primevue/toggleswitch';
  import Message from 'primevue/message';
  import { useToast } from 'primevue/usetoast';
  import { useConfirm } from 'primevue/useconfirm';

  useHead({ title: 'Translation - Jiten' });

  definePageMeta({
    middleware: ['auth-admin'],
  });

  interface SettingField {
    key: string;
    label: string;
    kind: 'text' | 'secret' | 'select' | 'model' | 'number';
    default: string | null;
    options: string[] | null;
    help: string | null;
    value: string | null;
    isSet: boolean;
    hint: string | null;
  }

  interface SettingGroup {
    id: string;
    name: string;
    fields: SettingField[];
  }

  interface TranslationSettings {
    cardMode: string;
    pageMode: string;
    pageAutoAi: boolean;
    activeProvider: string;
    contextLines: number;
    prompt: string;
    defaultPrompt: string;
    providers: SettingGroup[];
    subtitleSources: SettingGroup[];
  }

  interface TestResult {
    text: string;
    model: string;
    inputTokens: number | null;
    outputTokens: number | null;
    elapsedMs: number;
  }

  interface Stats {
    aiTranslations: number;
    byModel: { provider: string; model: string; count: number }[];
    englishTracks: number;
  }

  const CARD_MODES = [
    { value: 'subtitlesThenAi', label: 'Subtitles, then AI' },
    { value: 'subtitlesOnly', label: 'Subtitles only' },
    { value: 'off', label: 'Off' },
  ];
  const PAGE_MODES = [
    { value: 'off', label: 'Off' },
    { value: 'button', label: 'Button per sentence' },
    { value: 'auto', label: 'Automatic' },
  ];

  const { $api } = useNuxtApp();
  const toast = useToast();
  const confirm = useConfirm();

  const settings = ref<TranslationSettings | null>(null);
  // Field edits per group id; secrets start empty (null = keep the saved value).
  const edits = ref<Record<string, Record<string, string | null>>>({});
  const models = ref<Record<string, string[]>>({});
  const loadingModels = ref<string | null>(null);
  const saving = ref(false);
  const testSentence = ref('お前ならできる！');
  const testing = ref(false);
  const testResult = ref<TestResult | null>(null);
  const stats = ref<Stats | null>(null);

  // PrimeVue's Select shows an empty-string value as nothing selected, so "no provider" is 'none' here and '' in the settings.
  const providerOptions = computed(() => [{ id: 'none', name: 'None (subtitles only)' }, ...(settings.value?.providers ?? [])]);
  const providerChoice = computed({
    get: () => settings.value?.activeProvider || 'none',
    set: (id: string) => settings.value && (settings.value.activeProvider = id === 'none' ? '' : id),
  });
  const activeProvider = computed(() => settings.value?.providers.find((p) => p.id === settings.value?.activeProvider) ?? null);
  const promptIsDefault = computed(() => settings.value?.prompt.trim() === settings.value?.defaultPrompt.trim());

  function load(data: TranslationSettings) {
    settings.value = data;
    edits.value = Object.fromEntries(
      [...data.providers, ...data.subtitleSources].map((g) => [
        g.id,
        Object.fromEntries(g.fields.map((f) => [f.key, f.kind === 'secret' ? null : (f.value ?? '')])),
      ])
    );
  }

  async function refresh() {
    try {
      load(await $api<TranslationSettings>('admin/translation/settings'));
      stats.value = await $api<Stats>('admin/translation/stats');
    } catch (e) {
      toast.add({ severity: 'error', summary: 'Error', detail: extractApiError(e, 'Failed to load the settings'), life: 5000 });
    }
  }

  onMounted(refresh);

  function groupEdits(groups: SettingGroup[]) {
    return Object.fromEntries(groups.map((g) => [g.id, edits.value[g.id] ?? {}]));
  }

  async function save() {
    if (!settings.value) return;
    saving.value = true;
    try {
      const s = settings.value;
      load(
        await $api<TranslationSettings>('admin/translation/settings', {
          method: 'PUT',
          body: {
            cardMode: s.cardMode,
            pageMode: s.pageMode,
            pageAutoAi: s.pageAutoAi,
            activeProvider: s.activeProvider,
            contextLines: s.contextLines,
            prompt: s.prompt,
            providers: groupEdits(s.providers),
            subtitleSources: groupEdits(s.subtitleSources),
          },
        })
      );
      toast.add({ severity: 'success', summary: 'Saved', life: 2500 });
    } catch (e) {
      toast.add({ severity: 'error', summary: 'Error', detail: extractApiError(e, 'Failed to save'), life: 5000 });
    } finally {
      saving.value = false;
    }
  }

  function clearSecret(groupId: string, key: string) {
    edits.value[groupId]![key] = '';
  }

  async function loadModels(providerId: string) {
    loadingModels.value = providerId;
    try {
      const { models: list } = await $api<{ models: string[] }>(`admin/translation/providers/${providerId}/models`);
      models.value = { ...models.value, [providerId]: list };
    } catch (e) {
      toast.add({ severity: 'error', summary: 'Error', detail: extractApiError(e, "Couldn't list the models"), life: 5000 });
    } finally {
      loadingModels.value = null;
    }
  }

  function modelOptions(group: SettingGroup, field: SettingField): string[] {
    const current = edits.value[group.id]?.[field.key];
    const list = models.value[group.id] ?? [];
    return current && !list.includes(current) ? [current, ...list] : list;
  }

  async function test() {
    testing.value = true;
    testResult.value = null;
    try {
      testResult.value = await $api<TestResult>('admin/translation/test', { method: 'POST', body: { sentence: testSentence.value } });
    } catch (e) {
      toast.add({ severity: 'error', summary: 'Test failed', detail: extractApiError(e, 'The provider returned an error'), life: 8000 });
    } finally {
      testing.value = false;
    }
  }

  function clearCache() {
    confirm.require({
      message: 'Delete every cached AI translation? Sentences will be translated again (and billed again) when next needed.',
      header: 'Clear AI translations',
      acceptProps: { label: 'Delete', severity: 'danger' },
      rejectProps: { label: 'Cancel', severity: 'secondary', outlined: true },
      accept: async () => {
        try {
          const { deleted } = await $api<{ deleted: number }>('admin/translation/cache', { method: 'DELETE' });
          toast.add({ severity: 'success', summary: `Deleted ${deleted} translations`, life: 3000 });
          stats.value = await $api<Stats>('admin/translation/stats');
        } catch (e) {
          toast.add({ severity: 'error', summary: 'Error', detail: extractApiError(e, 'Failed to clear'), life: 5000 });
        }
      },
    });
  }
</script>

<template>
  <div class="container mx-auto p-4">
    <div class="flex items-center mb-6">
      <Button icon="pi pi-arrow-left" class="p-button-text mr-2" @click="navigateTo('/dashboard')" />
      <h1 class="text-3xl font-bold">Translation</h1>
    </div>

    <div v-if="!settings" class="h-40 rounded bg-surface-100 dark:bg-surface-800 animate-pulse" />

    <div v-else class="flex flex-col gap-6 max-w-3xl">
      <Card class="shadow-md">
        <template #title>Where English appears</template>
        <template #content>
          <p class="text-sm text-gray-600 dark:text-gray-300 mb-4">
            English comes from a title's English subtitles first (found per title on its admin page), then from the AI provider below. AI translations are
            cached per sentence, and only administrators can trigger new ones.
          </p>
          <div class="flex flex-col gap-4">
            <div>
              <label class="font-semibold block mb-1">On Anki cards ("Sentence (English)" field)</label>
              <SelectButton v-model="settings.cardMode" :options="CARD_MODES" option-label="label" option-value="value" :allow-empty="false" />
            </div>
            <div>
              <label class="font-semibold block mb-1">On the Sentences page</label>
              <SelectButton v-model="settings.pageMode" :options="PAGE_MODES" option-label="label" option-value="value" :allow-empty="false" />
              <p class="text-xs text-gray-500 mt-1">
                Automatic shows subtitle and already cached English for every sentence. The button asks for one sentence at a time, using the AI when needed.
              </p>
            </div>
            <div v-if="settings.pageMode === 'auto'" class="flex items-center gap-2">
              <ToggleSwitch v-model="settings.pageAutoAi" input-id="page-auto-ai" />
              <label for="page-auto-ai">Also call the AI automatically for sentences without English (costs per sentence shown)</label>
            </div>
          </div>
        </template>
      </Card>

      <Card class="shadow-md">
        <template #title>AI provider</template>
        <template #content>
          <div class="flex flex-col gap-4">
            <div>
              <label class="font-semibold block mb-1">Used for sentences without subtitle English</label>
              <Select v-model="providerChoice" :options="providerOptions" option-label="name" option-value="id" class="w-full md:w-80" />
            </div>

            <div v-for="group in settings.providers" :key="group.id" class="border border-surface-200 dark:border-surface-700 rounded p-3">
              <h3 class="font-semibold mb-2">
                {{ group.name }}
                <span v-if="group.id === settings.activeProvider" class="text-xs font-normal text-primary-600 ml-1">active</span>
              </h3>
              <div class="flex flex-col gap-3">
                <div v-for="field in group.fields" :key="field.key">
                  <label class="text-sm block mb-1" :for="`${group.id}-${field.key}`">{{ field.label }}</label>
                  <div v-if="field.kind === 'secret'" class="flex items-center gap-2">
                    <Password
                      v-model="edits[group.id]![field.key]"
                      :input-id="`${group.id}-${field.key}`"
                      :feedback="false"
                      toggle-mask
                      :placeholder="field.isSet ? `Saved (${field.hint}); type to replace` : 'Not set'"
                      class="w-full md:w-96"
                      input-class="w-full"
                    />
                    <Button v-if="field.isSet" label="Remove" size="small" severity="secondary" text @click="clearSecret(group.id, field.key)" />
                  </div>
                  <div v-else-if="field.kind === 'model'" class="flex items-center gap-2">
                    <Select
                      v-model="edits[group.id]![field.key]"
                      :input-id="`${group.id}-${field.key}`"
                      :options="modelOptions(group, field)"
                      editable
                      filter
                      class="w-full md:w-96"
                    />
                    <Button
                      label="Load models"
                      size="small"
                      severity="secondary"
                      outlined
                      :loading="loadingModels === group.id"
                      :disabled="!group.fields.find((f) => f.kind === 'secret')?.isSet"
                      @click="loadModels(group.id)"
                    />
                  </div>
                  <Select
                    v-else-if="field.kind === 'select'"
                    v-model="edits[group.id]![field.key]"
                    :input-id="`${group.id}-${field.key}`"
                    :options="field.options ?? []"
                    class="w-full md:w-60"
                  />
                  <InputText v-else v-model="edits[group.id]![field.key]" :id="`${group.id}-${field.key}`" class="w-full md:w-96" />
                  <p v-if="field.help" class="text-xs text-gray-500 mt-1">{{ field.help }}</p>
                </div>
              </div>
            </div>

            <div>
              <label class="font-semibold block mb-1" for="context-lines">Context lines</label>
              <InputNumber v-model="settings.contextLines" input-id="context-lines" :min="0" :max="20" show-buttons class="w-32" />
              <p class="text-xs text-gray-500 mt-1">Japanese lines sent before and after the sentence, from the episode's subtitles.</p>
            </div>

            <div>
              <div class="flex items-center justify-between mb-1">
                <label class="font-semibold" for="prompt">Prompt</label>
                <Button
                  label="Reset to GSM default"
                  size="small"
                  severity="secondary"
                  text
                  :disabled="promptIsDefault"
                  @click="settings.prompt = settings.defaultPrompt"
                />
              </div>
              <Textarea id="prompt" v-model="settings.prompt" rows="14" class="w-full font-mono text-xs" />
              <p class="text-xs text-gray-500 mt-1">
                GameSentenceMiner's translation prompt. <code>{title}</code>, <code>{context}</code> and <code>{sentence}</code> are filled in per sentence.
              </p>
            </div>
          </div>
        </template>
      </Card>

      <Card class="shadow-md">
        <template #title>Subtitle sources</template>
        <template #content>
          <p class="text-sm text-gray-600 dark:text-gray-300 mb-3">Keys for looking up English subtitles on a title's admin page.</p>
          <div class="flex flex-col gap-3">
            <div
              v-for="group in settings.subtitleSources.filter((g) => g.fields.length)"
              :key="group.id"
              class="border border-surface-200 dark:border-surface-700 rounded p-3"
            >
              <h3 class="font-semibold mb-2">{{ group.name }}</h3>
              <div class="flex flex-col gap-3">
                <div v-for="field in group.fields" :key="field.key">
                  <label class="text-sm block mb-1" :for="`${group.id}-${field.key}`">{{ field.label }}</label>
                  <div v-if="field.kind === 'secret'" class="flex items-center gap-2">
                    <Password
                      v-model="edits[group.id]![field.key]"
                      :input-id="`${group.id}-${field.key}`"
                      :feedback="false"
                      toggle-mask
                      :placeholder="field.isSet ? `Saved (${field.hint}); type to replace` : 'Not set'"
                      class="w-full md:w-96"
                      input-class="w-full"
                    />
                    <Button v-if="field.isSet" label="Remove" size="small" severity="secondary" text @click="clearSecret(group.id, field.key)" />
                  </div>
                  <InputText v-else v-model="edits[group.id]![field.key]" :id="`${group.id}-${field.key}`" class="w-full md:w-96" />
                  <p v-if="field.help" class="text-xs text-gray-500 mt-1">{{ field.help }}</p>
                </div>
              </div>
            </div>
          </div>
        </template>
      </Card>

      <div class="flex gap-2">
        <Button label="Save" icon="pi pi-check" :loading="saving" @click="save" />
      </div>

      <Card class="shadow-md">
        <template #title>Test</template>
        <template #content>
          <p class="text-sm text-gray-600 dark:text-gray-300 mb-3">
            Translates a line with the saved settings{{ activeProvider ? ` (${activeProvider.name})` : '' }}. Save changes first.
          </p>
          <div class="flex flex-col sm:flex-row gap-2">
            <InputText v-model="testSentence" class="flex-1" lang="ja" />
            <Button label="Translate" icon="pi pi-language" :loading="testing" :disabled="!settings.activeProvider" @click="test" />
          </div>
          <Message v-if="testResult" severity="success" class="mt-3">
            <div class="font-semibold">{{ testResult.text }}</div>
            <div class="text-xs mt-1">
              {{ testResult.model }} · {{ (testResult.elapsedMs / 1000).toFixed(1) }} s
              <template v-if="testResult.inputTokens != null"> · {{ testResult.inputTokens }} in / {{ testResult.outputTokens }} out tokens</template>
            </div>
          </Message>
        </template>
      </Card>

      <Card class="shadow-md">
        <template #title>Cache</template>
        <template #content>
          <div v-if="stats" class="text-sm flex flex-col gap-1 mb-3">
            <span>{{ stats.englishTracks }} episodes with English subtitles</span>
            <span>{{ stats.aiTranslations }} cached AI translations</span>
            <span v-for="m in stats.byModel" :key="`${m.provider}-${m.model}`" class="text-gray-500 ml-3">{{ m.model }}: {{ m.count }}</span>
          </div>
          <Button
            label="Clear AI translations"
            icon="pi pi-trash"
            severity="danger"
            outlined
            size="small"
            :disabled="!stats?.aiTranslations"
            @click="clearCache"
          />
          <p class="text-xs text-gray-500 mt-2">For one title only, or to translate a whole title in advance, use the title's admin page.</p>
        </template>
      </Card>
    </div>
  </div>
</template>
