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
          groups: [{ name: 'vendor', test: /node_modules[\\/]/ }],
        },
      },
    },
  },
  server: { port: 5173, proxy: { '/api': 'http://127.0.0.1:5179', '/health': 'http://127.0.0.1:5179' } },
})
