import { defineConfig } from 'vite'
import { svelte } from '@sveltejs/vite-plugin-svelte'

// https://vite.dev/config/
export default defineConfig({
  plugins: [svelte()],
    server: {
        port: 5173,
        proxy: {
            '/api': {
                target: 'http://localhost:5000',   // ← порт твоего API
                changeOrigin: true
            },
            '/hubs': {
                target: 'http://localhost:5000',   // ← для SignalR
                changeOrigin: true,
                ws: true                            // ← поддержка WebSocket
            }
        }
    }
})
