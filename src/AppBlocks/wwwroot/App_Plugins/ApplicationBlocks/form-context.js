// Block grids keep content in a flat collection and nesting in layout areas. A field belongs to its
// enclosing form, which can be any of several forms on a website page.
export function enclosingFormResource(contents, layouts, contentKey, parentKey) {
    if (!contentKey) return undefined;
    const data = new Map((contents ?? []).map(content => [content.key, content]));
    function find(items, key, owner) {
        for (const item of items ?? []) {
            const configured = data.get(item.contentKey)?.values?.find(value => value.alias === "formInterface");
            const current = configured ?? owner;
            if (item.contentKey === key) return { owner: current };
            for (const area of item.areas ?? []) {
                const found = find(area.items, key, current);
                if (found) return found;
            }
        }
        return undefined;
    }
    try {
        // Non-live block editing does not insert a new field until Submit. Its containing area's
        // parent is already in the grid, so use that path while the field itself is still local.
        const match = find(layouts, contentKey) ?? (parentKey ? find(layouts, parentKey) : undefined);
        const raw = match?.owner?.value;
        const value = typeof raw === "string" ? JSON.parse(raw) : raw;
        return value?.contentData?.[0]?.values?.find(property => property.alias === "resource")?.value;
    } catch { return undefined; }
}
