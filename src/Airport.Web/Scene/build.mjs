import { build } from 'esbuild';
import { mkdir, readFile, writeFile } from 'node:fs/promises';

await mkdir('wwwroot/js', { recursive: true });
await build({
  entryPoints: ['Scene/airport-scene.js'],
  outfile: 'wwwroot/js/airport-scene.js',
  bundle: true,
  minify: true,
  format: 'esm',
  target: ['es2022'],
  legalComments: 'external',
  banner: { js: '/* Airport procedural renderer. Third-party license: ./three-LICENSE.txt */' },
});
await writeFile('wwwroot/js/three-LICENSE.txt', await readFile('node_modules/three/LICENSE'));
