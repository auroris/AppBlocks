import { UmbConditionBase } from "@umbraco-cms/backoffice/extension-registry";
import { UMB_AUTH_CONTEXT } from "@umbraco-cms/backoffice/auth";
import { request } from "./api.js";

// Administrators and the User Groups AppBlocks:AdministratorGroups names; the server asks the same question on every request.
export default class CodeEditorCondition extends UmbConditionBase {
    constructor(host, args) {
        super(host, args);
        this.consumeContext(UMB_AUTH_CONTEXT, async auth => {
            this.permitted = false;
            if (!auth) return;
            try { this.permitted = (await request("code/access")).allowed; }
            catch { this.permitted = false; }
        });
    }
}
