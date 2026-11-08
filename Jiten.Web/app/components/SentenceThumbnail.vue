<script setup lang="ts">
  import Dialog from 'primevue/dialog';

  /**
   * A sentence's screenshot, loaded when it scrolls into view. Clicking it opens the clip with sound, playing once, or the
   * enlarged screenshot when there's no clip. Renders nothing when there's no picture.
   */
  const props = withDefaults(defineProps<{ sentenceId: number; clipKey?: string; pictureKey?: string }>(), { clipKey: '', pictureKey: '' });

  const { load } = useSentencePicture();
  const { load: loadVideo } = useSentenceVideo();
  const root = ref<HTMLElement | null>(null);
  const url = ref<string | null>(null);
  const missing = ref(false);
  const enlarged = ref(false);
  const video = ref<string | null>(null);
  const videoLoading = ref(false);
  let observer: IntersectionObserver | null = null;

  onMounted(() => {
    observer = new IntersectionObserver(
      async (entries) => {
        if (!entries.some((e) => e.isIntersecting)) return;
        observer?.disconnect();
        url.value = await load(props.sentenceId, props.pictureKey);
        missing.value = url.value == null;
      },
      { rootMargin: '200px' }
    );
    if (root.value) observer.observe(root.value);
  });

  onBeforeUnmount(() => observer?.disconnect());

  async function open() {
    enlarged.value = true;
    if (video.value) return;
    videoLoading.value = true;
    try {
      video.value = await loadVideo(props.sentenceId, props.clipKey);
    } finally {
      videoLoading.value = false;
    }
  }
</script>

<template>
  <div v-if="!missing" ref="root" class="aspect-video rounded overflow-hidden bg-surface-100 dark:bg-surface-800">
    <button v-if="url" type="button" class="relative block w-full h-full cursor-pointer" aria-label="Play the clip" @click="open">
      <img :src="url" alt="Screenshot of the sentence" class="w-full h-full object-cover" />
      <span class="absolute right-1 bottom-1 w-5 h-5 rounded-full bg-black/60 text-white grid place-items-center" aria-hidden="true">
        <i class="pi pi-play" style="font-size: 0.55rem" />
      </span>
    </button>
    <Dialog v-model:visible="enlarged" modal dismissable-mask :show-header="false" class="max-w-3xl w-[calc(100vw-2rem)]" :content-style="{ padding: 0 }">
      <video v-if="video" :src="video" :poster="url ?? undefined" controls autoplay playsinline class="w-full block bg-black" />
      <div v-else class="relative">
        <img v-if="url" :src="url" alt="Screenshot of the sentence" class="w-full cursor-zoom-out" @click="enlarged = false" />
        <i v-if="videoLoading" class="pi pi-spin pi-spinner absolute inset-0 m-auto w-fit h-fit text-white text-2xl" aria-label="Loading the clip" />
      </div>
    </Dialog>
  </div>
</template>
