import { onMounted, onUnmounted } from 'vue'

export function useAutoRefresh(refresh: () => unknown, milliseconds = 5000): void {
  let timer: ReturnType<typeof setInterval>
  function onVisible() {
    if (document.visibilityState === 'visible') void refresh()
  }
  onMounted(() => {
    void refresh()
    timer = setInterval(onVisible, milliseconds)
    document.addEventListener('visibilitychange', onVisible)
  })
  onUnmounted(() => {
    clearInterval(timer)
    document.removeEventListener('visibilitychange', onVisible)
  })
}
