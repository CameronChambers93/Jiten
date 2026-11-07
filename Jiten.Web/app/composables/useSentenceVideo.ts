// Object URLs by sentence, shared by every player on the page; null means the sentence has no clip. Only the latest few
// clips are kept, since each is a few hundred KB.
const KEEP = 20;
const videos = new Map<number, string | null>();
const requests = new Map<number, Promise<string | null>>();

/** A sentence's clip with sound, cut from its linked video on first use, padded like the card audio. */
export function useSentenceVideo() {
  const { $api } = useNuxtApp();

  function load(sentenceId: number): Promise<string | null> {
    if (videos.has(sentenceId)) return Promise.resolve(videos.get(sentenceId)!);
    let request = requests.get(sentenceId);
    if (!request) {
      request = $api<Blob>(`sentence-media/${sentenceId}/video`, { responseType: 'blob' })
        .then((blob) => {
          const url = URL.createObjectURL(blob);
          videos.set(sentenceId, url);
          for (const [id, old] of videos) {
            if (videos.size <= KEEP) break;
            videos.delete(id);
            if (old) URL.revokeObjectURL(old);
          }
          return url;
        })
        .catch((e: { statusCode?: number; response?: { status?: number } }) => {
          // Only a 404 is final; anything else may work next time.
          if (e?.statusCode === 404 || e?.response?.status === 404) videos.set(sentenceId, null);
          return null;
        })
        .finally(() => requests.delete(sentenceId));
      requests.set(sentenceId, request);
    }
    return request;
  }

  return { load };
}
