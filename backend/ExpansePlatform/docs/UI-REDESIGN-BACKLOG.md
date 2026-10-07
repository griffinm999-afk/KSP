# Expanse Foundations UI redesign list

## Depot registration window — priority: high

**Observed in the user's September 26 in-game screenshot:** The tank rows are cut off horizontally, so part names, IDs, and resource amounts cannot be read together. The selection controls are tiny, parts have no useful grouping, and the fixed window devotes much of its height to empty anchor and resource sections before anything is selected. The register action is not visible in the captured window. This makes choosing the correct tanks difficult on a large station and risks including a docked visitor or omitting depot storage.

**Redesign target:** Give this workflow a resizable or responsive window with readable rows and columns for part name, resource summary, and selection. Group or filter candidates by resource and vessel section where KSP makes that reliable; never silently select visitor parts. Keep the selected count, capacity preview, anchor choice, and **Register depot** action visible or clearly reachable. Show long names and IDs through wrapping or a detail view instead of clipping them. Collapse empty preview sections until a tank is selected. Provide a clear confirmation of exactly which tanks will become depot members.

**Acceptance:** On the user's large fuel depot at normal game resolution, the user can distinguish similarly named tanks, review every selected part and its fuel, choose an anchor, and complete registration without guessing at clipped text or hunting for an offscreen button. Verify with actual in-game clicks and a docked visitor present; the M2 automated fixture did not exercise those UI interactions.

Reference: user screenshot `C:\Users\griff\AppData\Local\Temp\codex-clipboard-bf90193f-fe6e-4976-8cc2-112c40ba76cb.png`.
