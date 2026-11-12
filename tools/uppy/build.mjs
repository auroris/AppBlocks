import { build } from 'esbuild';
import { readFile, writeFile, readdir } from 'node:fs/promises';
import { resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const here = fileURLToPath(new URL('.', import.meta.url));
const output = resolve(here, '../../src/AppBlocks/wwwroot/App_Plugins/ApplicationBlocks');
const result = await build({
    absWorkingDir: here, entryPoints: ['entry.js'], outfile: resolve(output, 'uppy.js'),
    bundle: true, format: 'esm', target: 'es2022', minify: true, metafile: true,
    legalComments: 'inline', banner: { js: '/* Bundled by tools/uppy/build.mjs. Licenses: uppy-LICENSE.txt */' },
});

const packages = new Set(Object.keys(result.metafile.inputs)
    .map(path => /^(.*node_modules\/(?:@[^/]+\/)?[^/]+)\//.exec(path)?.[1]).filter(Boolean));
const notices = [];
for (const folder of [...packages].sort()) {
    const directory = resolve(here, folder);
    const pkg = JSON.parse(await readFile(resolve(directory, 'package.json'), 'utf8'));
    const license = (await readdir(directory)).find(name => /^licen[sc]e(?:\.(?:txt|md))?$/i.test(name));
    const text = license ? await readFile(resolve(directory, license), 'utf8')
        : (await readFile(resolve(directory, 'README.md'), 'utf8')).match(/^## Licen[sc]e[^\n]*\n([\s\S]*)/m)?.[1];
    if (!text) throw new Error(`Missing license for ${pkg.name}`);
    notices.push(`${pkg.name} ${pkg.version}\n${text}`);
}
await writeFile(resolve(output, 'uppy-LICENSE.txt'), notices.join('\n\n--------------------\n\n'));
