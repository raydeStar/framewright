import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'

export default defineConfig({
  plugins: [react()],
  build: {
    outDir: '../StoryboardStudio.Api/wwwroot',
    emptyOutDir: true,
    rolldownOptions: {
      output: {
        codeSplitting: {
          groups: [
            // three.js is only needed once an artist opens a model, so it keeps
            // its own chunk rather than riding along in the shared vendor bundle
            // that every page load pays for.
            { name: 'three', test: /node_modules[\\/]three[\\/]/ },
            { name: 'vendor', test: /node_modules[\\/]/ },
          ],
        },
      },
    },
  },
  server: { port: 5173, proxy: { '/api': 'http://127.0.0.1:5179', '/health': 'http://127.0.0.1:5179' } },
})
