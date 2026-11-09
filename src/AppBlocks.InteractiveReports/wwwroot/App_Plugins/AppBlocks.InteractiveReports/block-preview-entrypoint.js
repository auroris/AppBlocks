import { nestedBlockPreviews } from "../ApplicationBlocks/block-preview.js";
import { renderReport } from "./block-preview.js";
export function onInit() {
    // appReport's schema key. The host also offers it in the Forms container's grid.
    nestedBlockPreviews.set("9c1967ea-a547-54e4-524c-02ac6c281a06", renderReport);
}
