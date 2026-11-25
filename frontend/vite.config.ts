import { fileURLToPath, URL } from 'node:url'
import vue from '@vitejs/plugin-vue'
import tailwindcss from '@tailwindcss/vite'
import { defineConfig } from 'vite'

export default defineConfig({
  plugins: [vue(), tailwindcss()],
  resolve: { alias: { '@': fileURLToPath(new URL('./src', import.meta.url)) } },
  server: {
    port: Number(process.env.PORT ?? 5173),
    proxy: {
      '/api': process.env.services__api__http__0 ?? process.env.API_URL ?? 'http://127.0.0.1:5278',
      '/openapi': process.env.services__api__http__0 ?? process.env.API_URL ?? 'http://127.0.0.1:5278',
    },
  },
})
