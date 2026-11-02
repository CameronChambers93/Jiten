import type { FlagReason } from '~/utils/minedImport';

export interface SentenceFlag {
  sentenceFlagId: number;
  sentenceId: number;
  /** Null when the flag came from a card and still needs one. */
  reason: FlagReason | null;
  note: string | null;
  createdAt: string;
  text: string | null;
  source: string | null;
  hasPage: boolean;
}

// Per-tab cache of the user's open flags, so every sentence on a page knows whether it's flagged without asking.
const flags = ref<SentenceFlag[]>([]);
const bySentence = computed(() => new Map(flags.value.map((f) => [f.sentenceId, f])));
let loading: Promise<void> | null = null;
let loaded = false;
let watchingLogout = false;

export function useSentenceFlags() {
  const { $api } = useNuxtApp();
  const authStore = useAuthStore();

  // Another account may sign in on this tab: forget the last one's flags.
  if (import.meta.client && !watchingLogout) {
    watchingLogout = true;
    // Detached, so the watch outlives the component that first asked.
    effectScope(true).run(() =>
      watch(
        () => authStore.isAuthenticated,
        (signedIn) => {
          if (signedIn) return;
          flags.value = [];
          loaded = false;
        }
      )
    );
  }

  /** Reads the open flags again; throws if the request fails. */
  function load(): Promise<void> {
    loading ??= $api<{ flags: SentenceFlag[] }>('sentence-flags')
      .then((res) => {
        flags.value = res.flags;
        loaded = true;
      })
      .finally(() => (loading = null));
    return loading;
  }

  /** Loads the flags once per tab; a failure leaves sentences shown as unflagged and lets a later mount retry. */
  function ensureLoaded() {
    if (!import.meta.client || loaded || !authStore.isAuthenticated) return;
    load().catch(() => {});
  }

  function flagFor(sentenceId: number): SentenceFlag | undefined {
    return bySentence.value.get(sentenceId);
  }

  return { flags: computed(() => flags.value), load, ensureLoaded, flagFor };
}
