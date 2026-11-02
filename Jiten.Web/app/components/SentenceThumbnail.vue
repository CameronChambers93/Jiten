<script setup lang="ts">
  import Dialog from 'primevue/dialog';

  /** A sentence's screenshot, loaded when it scrolls into view; click to enlarge. Renders nothing when there's no picture. */
  const props = defineProps<{ sentenceId: number }>();

  const { load } = useSentencePicture();
  const root = ref<HTMLElement | null>(null);
  const url = ref<string | null>(null);
  const missing = ref(false);
  const enlarged = ref(false);
  let observer: IntersectionObserver | null = null;

  onMounted(() => {
    observer = new IntersectionObserver(
      async (entries) => {
        if (!entries.some((e) => e.isIntersecting)) return;
        observer?.disconnect();
        url.value = await load(props.sentenceId);
        missing.value = url.value == null;
      },
      { rootMargin: '200px' }
    );
    if (root.value) observer.observe(root.value);
  });

  onBeforeUnmount(() => observer?.disconnect());
</script>

<template>
  <div v-if="!missing" ref="root" class="aspect-video rounded overflow-hidden bg-surface-100 dark:bg-surface-800">
    <button v-if="url" type="button" class="block w-full h-full cursor-zoom-in" aria-label="Enlarge the screenshot" @click="enlarged = true">
      <img :src="url" alt="Screenshot of the sentence" class="w-full h-full object-cover" />
    </button>
    <Dialog v-model:visible="enlarged" modal dismissable-mask :show-header="false" class="max-w-3xl w-[calc(100vw-2rem)]" :content-style="{ padding: 0 }">
      <img v-if="url" :src="url" alt="Screenshot of the sentence" class="w-full cursor-zoom-out" @click="enlarged = false" />
    </Dialog>
  </div>
</template>
