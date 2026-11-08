import { normalizePipelines, type MiningPipelines, type Pipeline, type PipelinePage } from '~/utils/pipelines';

// Shared by every sentence button and the Pipelines page; loaded once per app load, replaced on save.
const state = ref<MiningPipelines | null>(null);
let loading: Promise<MiningPipelines> | null = null;

/** The user's sentence pipelines (n1miner), from `api/mining/pipelines`. */
export function usePipelines() {
  const { $api } = useNuxtApp();

  async function load(force = false): Promise<MiningPipelines> {
    if (state.value && !force) return state.value;
    loading ??= $api<MiningPipelines>('mining/pipelines')
      .then((raw) => (state.value = normalizePipelines(raw)))
      .finally(() => (loading = null));
    return loading;
  }

  async function save(next: MiningPipelines): Promise<MiningPipelines> {
    const raw = await $api<MiningPipelines>('mining/pipelines', { method: 'PUT', body: next });
    state.value = normalizePipelines(raw);
    return state.value;
  }

  /** The page's default pipeline first, then the others in their order. Empty until loaded. */
  function forPage(page: PipelinePage): Pipeline[] {
    const all = state.value?.pipelines ?? [];
    const id = state.value?.defaults[page];
    const first = all.find((p) => p.id === id) ?? all[0];
    return first ? [first, ...all.filter((p) => p !== first)] : [];
  }

  /** The padding for listening to a line: the default word page pipeline's first Build step, else 250 ms. */
  const previewPadding = computed(() => {
    const build = forPage('word')[0]?.steps.find((s) => s.kind === 'build');
    return build?.kind === 'build' ? { padStartMs: build.padStartMs, padEndMs: build.padEndMs } : { padStartMs: 250, padEndMs: 250 };
  });

  return { pipelines: readonly(state), load, save, forPage, previewPadding };
}
