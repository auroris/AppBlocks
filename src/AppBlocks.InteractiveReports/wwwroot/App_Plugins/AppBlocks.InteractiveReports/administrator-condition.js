import { UmbConditionBase } from "@umbraco-cms/backoffice/extension-registry";
import { UMB_AUTH_CONTEXT } from "@umbraco-cms/backoffice/auth";
import { request } from "../AppBlocks/api.js";

export default class ReportAdministratorCondition extends UmbConditionBase {
    constructor(host, args) {
        super(host, args);
        this.consumeContext(UMB_AUTH_CONTEXT, async auth => {
            this.permitted = false;
            if (!auth) return;
            try { this.permitted = (await request("report-administration/access")).allowed; }
            catch { this.permitted = false; }
        });
    }
}
