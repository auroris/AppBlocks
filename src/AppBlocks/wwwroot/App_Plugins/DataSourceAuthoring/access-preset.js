import { UmbControllerBase } from "@umbraco-cms/backoffice/class-api";
import { UMB_WORKSPACE_CONTEXT } from "@umbraco-cms/backoffice/workspace";

// The SQL data source document type (Schema/ContentTypes/sqldatasource.config) and its mandatory Access property.
const sqlDataSource = "6a79805a-4170-811d-64a8-83ee1422bade";

/**
 * Starts a new SQL data source with Access set to Public: if you can see the page, you can see its data. Umbraco runs value
 * presets for every property without a value, also when it opens an existing document, so this answers only while the
 * workspace is creating one; an existing document shows what it stores, an empty value included, and publishing refuses that.
 */
export class DataSourceAccessPreset extends UmbControllerBase {
    async processValue(value, config, typeArgs, args) {
        if (value !== undefined || args?.alias !== "access" || args.entityTypeUnique?.toLowerCase() !== sqlDataSource) return value;
        const workspace = await Promise.race([
            this.getContext(UMB_WORKSPACE_CONTEXT), new Promise(resolve => setTimeout(resolve, 2000)),
        ]).catch(() => undefined);
        // Loading an existing document marks the workspace as not new before presets run; creating one marks it new afterwards.
        return workspace && workspace.getIsNew?.() !== false ? "Public" : value;
    }
}

export { DataSourceAccessPreset as api };
