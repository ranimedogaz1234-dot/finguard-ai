/// <reference types="vitest/config" />
import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';

// The production build is served by the ASP.NET Core API (same origin, so cookies stay SameSite=Strict).
export default defineConfig({
  plugins: [react()],
  build: { outDir: '../src/FinGuard.Api/wwwroot', emptyOutDir: true, sourcemap: false, chunkSizeWarningLimit: 900 },
  server: {
    port: 5173,
    strictPort: true,
    proxy: { '/api': 'http://localhost:5080', '/health': 'http://localhost:5080', '/scalar': 'http://localhost:5080', '/openapi': 'http://localhost:5080' },
  },
  test: {
    environment: 'jsdom',
    globals: true,
    setupFiles: ['./src/test/setup.ts'],
    include: ['src/**/*.test.{ts,tsx}'],
    css: false,
  },
});
