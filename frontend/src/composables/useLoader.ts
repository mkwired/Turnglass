import { onUnmounted, ref, shallowRef } from 'vue'
import { messageOf } from '@/lib/format'

export function useLoader<T>() {
  const data = shallowRef<T>()
  const loading = ref(false)
  const error = ref('')
  let requestId = 0
  onUnmounted(() => { requestId++ })

  async function load(fetcher: () => Promise<T>): Promise<boolean> {
    const id = ++requestId
    loading.value = true
    error.value = ''
    try {
      const value = await fetcher()
      if (id !== requestId) return false
      data.value = value
      return true
    } catch (reason) {
      if (id === requestId) error.value = messageOf(reason)
      return false
    } finally {
      if (id === requestId) loading.value = false
    }
  }
  return { data, loading, error, load }
}
