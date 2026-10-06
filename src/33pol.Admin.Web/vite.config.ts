import { defineConfig } from 'vite';
import solid from 'vite-plugin-solid';

export default defineConfig({
  plugins: [solid()],
  base: '/admin/',
  build: {
    outDir: '../33pol.App/wwwroot/admin',
    emptyOutDir: true,
    rollupOptions: {
      output: {
        manualChunks(id) {
          if (id.includes('/pages/ratelimits/')) return 'ratelimits';
        },
      },
    },
  },
});
