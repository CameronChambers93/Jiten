// Object URLs by sentence and frame, shared by every list on the page; null means the sentence has no picture.
const pictures = new Map<string, string | null>();
const requests = new Map<string, Promise<string | null>>();

/**
 * A sentence's screenshot, cut from its linked video on first use. A fixed frame has its own key (the sentence's
 * pictureKey), which also changes the URL, since the browser keeps a picture for a day.
 */
export function useSentencePicture() {
  const { $api } = useNuxtApp();

  function load(sentenceId: number, pictureKey = ''): Promise<string | null> {
    const key = `${sentenceId}${pictureKey}`;
    if (pictures.has(key)) return Promise.resolve(pictures.get(key)!);
    let request = requests.get(key);
    if (!request) {
      request = $api<Blob>(`sentence-media/${sentenceId}/picture`, { responseType: 'blob', query: pictureKey ? { v: pictureKey } : undefined })
        .then((blob) => {
          const url = URL.createObjectURL(blob);
          pictures.set(key, url);
          return url;
        })
        .catch((e: { statusCode?: number; response?: { status?: number } }) => {
          // Only a 404 is final; anything else may work next time.
          if (e?.statusCode === 404 || e?.response?.status === 404) pictures.set(key, null);
          return null;
        })
        .finally(() => requests.delete(key));
      requests.set(key, request);
    }
    return request;
  }

  return { load };
}
