import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';
import tailwindcss from '@tailwindcss/vite';

export default defineConfig({
  plugins: [react(), tailwindcss()],
  resolve: { dedupe: ['react', 'react-dom'] },
  build: {
    outDir: 'dist',
    target: ['es2020', 'edge88', 'firefox78', 'chrome87', 'safari14'],
  },
  assetsInclude: ['**/*.md'],
});
