// Object URLs by sentence, shared by every list on the page; null means the sentence has no picture.
const pictures = new Map<number, string | null>();
const requests = new Map<number, Promise<string | null>>();

/** A sentence's screenshot, cut from its linked video on first use. */
export function useSentencePicture() {
  const { $api } = useNuxtApp();

  function load(sentenceId: number): Promise<string | null> {
    if (pictures.has(sentenceId)) return Promise.resolve(pictures.get(sentenceId)!);
    let request = requests.get(sentenceId);
    if (!request) {
      request = $api<Blob>(`sentence-media/${sentenceId}/picture`, { responseType: 'blob' })
        .then((blob) => {
          const url = URL.createObjectURL(blob);
          pictures.set(sentenceId, url);
          return url;
        })
        .catch((e: { statusCode?: number; response?: { status?: number } }) => {
          // Only a 404 is final; anything else may work next time.
          if (e?.statusCode === 404 || e?.response?.status === 404) pictures.set(sentenceId, null);
          return null;
        })
        .finally(() => requests.delete(sentenceId));
      requests.set(sentenceId, request);
    }
    return request;
  }

  return { load };
}
