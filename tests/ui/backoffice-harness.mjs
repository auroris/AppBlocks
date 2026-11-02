import { readFile } from 'node:fs/promises';
import { resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = fileURLToPath(new URL('../../', import.meta.url));

// Use the shipped Umbraco HTTP client in isolated component tests; the clean-install suite exercises its real auth context.
export async function backofficeRoute(route, assets, token, pathBase = '/sub') {
    const path = new URL(route.request().url()).pathname;
    const js = body => route.fulfill({ contentType: 'text/javascript', body });
    if (path === '/http-client.js') {
        await js(`import { client } from '/native-backend.js';
            client.setConfig({ baseUrl: location.origin + ${JSON.stringify(pathBase)}, credentials: 'include', auth: () => ${JSON.stringify(token)} });
            export { client as umbHttpClient };`);
    } else if (path === '/native-backend.js') {
        const directory = assets.replaceAll('\\', '/').endsWith('/umbraco/backoffice') ? assets : resolve(assets, 'umbraco/backoffice');
        await js(await readFile(resolve(directory, 'packages/core/backend-api/index.js'), 'utf8'));
    } else if (path === '/repository.js') {
        await js('export class UmbRepositoryBase { constructor(host) { this.host = host; } }');
    } else if (path === '/workspace-harness.js') {
        const manifest = JSON.parse(await readFile(resolve(root, 'src/AppBlocks/wwwroot/App_Plugins/AppBlocks/umbraco-package.json'), 'utf8'));
        await js(`import '${pathBase}/App_Plugins/AppBlocks/dashboard.js';
            const views = ${JSON.stringify(manifest.extensions.filter(e => e.type === 'workspaceView'))};
            const element = document.querySelector('appblocks-dashboard');
            const navigation = document.createElement('nav');
            function open(pathname) { element.manifest = views.find(view => view.meta.pathname === pathname) || views[0]; }
            for (const view of views) {
                const tab = document.createElement('button'); tab.setAttribute('role', 'tab'); tab.textContent = view.meta.label;
                tab.onclick = () => open(view.meta.pathname); navigation.append(tab);
            }
            document.body.prepend(navigation);
            document.addEventListener('click', event => {
                const link = event.composedPath().find(node => node instanceof HTMLAnchorElement && node.href.includes('/workspace/appblocks/view/'));
                if (link) { event.preventDefault(); open(new URL(link.href).pathname.split('/').at(-1)); }
            });
            open(new URL(location.href).searchParams.get('view') || 'forms');`);
    } else return false;
    return true;
}
