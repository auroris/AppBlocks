import { UmbControllerBase } from "@umbraco-cms/backoffice/class-api";

// The choices of the "Application: Report record links" data type (Schema/DataTypes/ApplicationReportRecordLinks.config).
const choices = ["Open and create", "Open only", "Create only", "None"];

/**
 * Starts the Interactive Report block's Record links setting at Open and create. Umbraco runs value presets for every property
 * without a value, also when it opens an existing block; there an empty value already means Open and create, so showing it
 * selected changes nothing. Only a radio list offering exactly these choices is answered, whatever the property's alias.
 */
export class RecordLinksPreset extends UmbControllerBase {
    async processValue(value, config, typeArgs, args) {
        if (value !== undefined || args?.alias !== "recordLinks") return value;
        const items = (config?.find?.(c => c.alias === "items")?.value ?? []).map(item => typeof item === "string" ? item : item?.value);
        return items.length === choices.length && choices.every(choice => items.includes(choice)) ? choices[0] : value;
    }
}

export { RecordLinksPreset as api };
