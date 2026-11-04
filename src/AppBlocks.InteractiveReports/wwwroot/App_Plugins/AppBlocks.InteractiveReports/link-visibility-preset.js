import { UmbControllerBase } from "@umbraco-cms/backoffice/class-api";

// The choices of the "Application: Report link visibility" data type (Schema/DataTypes/ApplicationReportLinkVisibility.config).
const choices = ["Public", "Form page decides", "Groups", "Hidden"];

/**
 * Starts the Interactive Report block's Create button and Open link at Form page decides. Umbraco runs value presets for every
 * property without a value, also when it opens an existing block; there an empty value already means Form page decides, so
 * showing it selected changes nothing. Only a radio list offering exactly these choices is answered.
 */
export class LinkVisibilityPreset extends UmbControllerBase {
    async processValue(value, config, typeArgs, args) {
        if (value !== undefined || !["createVisibility", "openVisibility"].includes(args?.alias)) return value;
        const items = (config?.find?.(c => c.alias === "items")?.value ?? []).map(item => typeof item === "string" ? item : item?.value);
        return items.length === choices.length && choices.every(choice => items.includes(choice)) ? "Form page decides" : value;
    }
}

export { LinkVisibilityPreset as api };
