import type { MineSearch } from '~/utils/mineSearches';

// Shared by the Mine page's saved-search select and its run dialog; loaded once per app load, replaced on save.
const state = ref<MineSearch[] | null>(null);
let loading: Promise<MineSearch[]> | null = null;

/** The user's saved Mine searches, from `api/mining/searches`. */
export function useMineSearches() {
  const { $api } = useNuxtApp();

  async function load(force = false): Promise<MineSearch[]> {
    if (state.value && !force) return state.value;
    loading ??= $api<MineSearch[]>('mining/searches')
      .then((list) => (state.value = list))
      .finally(() => (loading = null));
    return loading;
  }

  /**
   * Changes the saved searches: reads the server's list first, so a search saved on another device meanwhile isn't
   * lost, applies `change`, and writes the whole list back. Throws with the server's message.
   */
  async function update(change: (list: MineSearch[]) => MineSearch[]): Promise<MineSearch[]> {
    const fresh = await load(true);
    state.value = await $api<MineSearch[]>('mining/searches', { method: 'PUT', body: change(fresh) });
    return state.value;
  }

  return { searches: computed(() => state.value ?? []), load, update };
}
