/** Where a line stands in its word's list: kept for Jiten only, or on the word's Anki card. Absent when not in the list. */
export type ListSentenceState = 'kept' | 'card';

const BATCH = 200;

function keyOf(wordId: number, readingIndex: number, sentenceId: number) {
  return `${wordId}:${readingIndex}:${sentenceId}`;
}

/**
 * The word's sentence list (n1miner): saving one of Jiten's lines to it, and which lines on the page are in it. Rows ask
 * for their state with `watch`; the requests of one tick go to the server together.
 */
export function useSentenceList() {
  const { $api } = useNuxtApp();
  const states = useState<Record<string, ListSentenceState>>('sentence-list-states', () => ({}));
  const asked = useState<Record<string, true>>('sentence-list-asked', () => ({}));
  const pending = useState<string[]>('sentence-list-pending', () => []);

  async function flush() {
    const keys = pending.value.splice(0);
    for (let i = 0; i < keys.length; i += BATCH) {
      const lines = keys.slice(i, i + BATCH).join(',');
      try {
        const found = await $api<{ wordId: number; readingIndex: number; sentenceId: number; state: ListSentenceState }[]>('mined/list/state', {
          query: { lines },
        });
        const next = { ...states.value };
        for (const f of found) next[keyOf(f.wordId, f.readingIndex, f.sentenceId)] = f.state;
        states.value = next;
      } catch {
        // The rows show as not in the list; the next row to ask for them tries again.
        const failed = new Set(keys.slice(i, i + BATCH));
        asked.value = Object.fromEntries(Object.entries(asked.value).filter(([key]) => !failed.has(key)));
      }
    }
  }

  /** Asks for a row's state, once per app load (this tab's changes update it); rows asking in the same tick share a request. */
  function watch(wordId: number, readingIndex: number, sentenceId: number) {
    if (sentenceId <= 0) return;
    const key = keyOf(wordId, readingIndex, sentenceId);
    if (asked.value[key]) return;
    asked.value[key] = true;
    if (pending.value.push(key) === 1) queueMicrotask(flush);
  }

  function stateOf(wordId: number | undefined, readingIndex: number | undefined, sentenceId: number): ListSentenceState | undefined {
    if (wordId == null || readingIndex == null) return undefined;
    return states.value[keyOf(wordId, readingIndex, sentenceId)];
  }

  function setState(wordId: number, readingIndex: number, sentenceId: number, state: ListSentenceState) {
    states.value = { ...states.value, [keyOf(wordId, readingIndex, sentenceId)]: state };
  }

  /** Drops what is known of the line on the word's list, after the server took it off (it went onto another entry's card). */
  function forget(wordId: number, readingIndex: number, sentenceId: number) {
    const { [keyOf(wordId, readingIndex, sentenceId)]: _, ...rest } = states.value;
    states.value = rest;
  }

  /** Saves the line to the word's list, kept for Jiten; a line already there keeps its state. Throws on failure. */
  async function save(wordId: number, readingIndex: number, sentenceId: number, position: number, length: number): Promise<ListSentenceState> {
    const result = await $api<{ minedSentenceId: number; added: boolean; state: ListSentenceState }>('mined/list', {
      method: 'POST',
      body: { wordId, readingIndex, exampleSentenceId: sentenceId, position, length },
    });
    setState(wordId, readingIndex, sentenceId, result.state);
    return result.state;
  }

  return { watch, stateOf, setState, forget, save };
}
