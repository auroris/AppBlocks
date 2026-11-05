import { nestedBlockPreviews } from "../ApplicationBlocks/block-preview.js";
import { renderReport } from "./block-preview.js";
export function onInit() {
    // appReport's schema key. The host also offers it in the Forms container's grid.
    nestedBlockPreviews.set("067680b2-58b7-3fd9-a43d-d7e41f31ccde", renderReport);
}
