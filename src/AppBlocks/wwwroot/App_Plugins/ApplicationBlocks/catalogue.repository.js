import { UmbRepositoryBase } from "@umbraco-cms/backoffice/repository";
import { managementRequest } from "../AppBlocks/api.js";

// Share concurrent picker loads without retaining a signed-out user's catalogue. Expiry lets editors see newly published sources.
const catalogues = new WeakMap();
const observedSessions = new WeakSet();
/** Drops the shared catalogue, so the next picker sees the classes the C# section has just compiled. */
export function forgetCatalogue(auth) { if (auth) catalogues.delete(auth); }
export class AppBlocksCatalogueRepository extends UmbRepositoryBase {
    requestCatalogue(auth) {
        if (auth.isAuthorized && !observedSessions.has(auth)) {
            observedSessions.add(auth);
            // One subscription owned by the auth context's lifetime, shared by all picker repositories.
            auth.isAuthorized.subscribe(authorized => { if (!authorized) catalogues.delete(auth); });
        }
        let entry = catalogues.get(auth);
        if (!entry || entry.expires <= Date.now()) {
            entry = { expires: Infinity };
            entry.promise = managementRequest("application-blocks/catalogue").then(data => {
                entry.expires = Date.now() + 30_000;
                return data;
            }).catch(error => {
                if (catalogues.get(auth) === entry) catalogues.delete(auth);
                throw error;
            });
            catalogues.set(auth, entry);
        }
        return entry.promise;
    }
}
