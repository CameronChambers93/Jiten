// Object URLs by sentence, shared by every player on the page; null means the sentence has no clip. Only the latest few
// clips are kept, since each is a few hundred KB.
const KEEP = 20;
const videos = new Map<string, string | null>();
const requests = new Map<string, Promise<string | null>>();

/**
 * A sentence's clip with sound, cut from its linked video on first use, padded like the card audio. A fixed clip has its
 * own key (the sentence's clipKey), which also changes the URL, since the browser keeps a clip for a day.
 */
export function useSentenceVideo() {
  const { $api } = useNuxtApp();

  function load(sentenceId: number, clipKey = ''): Promise<string | null> {
    const key = `${sentenceId}${clipKey}`;
    if (videos.has(key)) return Promise.resolve(videos.get(key)!);
    let request = requests.get(key);
    if (!request) {
      request = $api<Blob>(`sentence-media/${sentenceId}/video`, { responseType: 'blob', query: clipKey ? { v: clipKey } : undefined })
        .then((blob) => {
          const url = URL.createObjectURL(blob);
          videos.set(key, url);
          for (const [id, old] of videos) {
            if (videos.size <= KEEP) break;
            videos.delete(id);
            if (old) URL.revokeObjectURL(old);
          }
          return url;
        })
        .catch((e: { statusCode?: number; response?: { status?: number } }) => {
          // Only a 404 is final; anything else may work next time.
          if (e?.statusCode === 404 || e?.response?.status === 404) videos.set(key, null);
          return null;
        })
        .finally(() => requests.delete(key));
      requests.set(key, request);
    }
    return request;
  }

  return { load };
}
