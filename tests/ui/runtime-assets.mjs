import { readFile } from 'node:fs/promises';
import { basename, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

// The public runtime's files from the source tree: runtime.js and the modules it imports, and runtime.css. A test page
// routes every request below App_Plugins/ApplicationBlocks/ here, at any path base, and gets null for anything else.
const folder = fileURLToPath(new URL('../../src/AppBlocks/wwwroot/App_Plugins/ApplicationBlocks/', import.meta.url));
const types = { '.js': 'text/javascript', '.css': 'text/css' };

export async function runtimeAsset(pathname) {
    const match = /\/App_Plugins\/ApplicationBlocks\/([\w-]+(\.js|\.css))$/.exec(pathname);
    if (!match) return null;
    try { return { contentType: types[match[2]], body: await readFile(resolve(folder, basename(match[1])), 'utf8') }; }
    catch { return { status: 404, body: '' }; }
}
