import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';

// `npm run dev` forwards /api to the Creo API (Cadflow.Api, port 18090).
export default defineConfig({
  plugins: [react()],
  server: { port: 5174, proxy: { '/api': 'http://localhost:18090' } },
  build: { outDir: 'dist', emptyOutDir: true },
});
