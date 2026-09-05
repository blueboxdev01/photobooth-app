import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'

// Builds straight into the server's wwwroot so a published .exe serves the UI
// with no extra step. wwwroot is gitignored -- it is build output.
export default defineConfig({
  plugins: [react()],
  build: {
    outDir: '../Photobooth.Server/wwwroot',
    emptyOutDir: true,
  },
  server: {
    // `npm run dev` proxies to the running backend for frontend-only work.
    proxy: {
      '/api': 'http://localhost:5000',
      '/hub': { target: 'http://localhost:5000', ws: true },
    },
  },
})
