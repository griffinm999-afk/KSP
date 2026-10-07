# Fixed native Ranger Generator qualification

Status on 2026-10-03: root authorized integration after native12 freeze. Reviewed source is now in Domain and WorldBridge, with the original stage retained under `run/fixed-generator-next`. No KSP process, live install, selected save or frozen artifact was changed for this work. Compilation and unit/metadata checks are not native acceptance.

## Installed contract

The installed MKS `Ranger_PowerPack` contains one standalone units-based `USI_Converter`: local `Plutonium-238` input 0.000001/s, `ElectricCharge` output 50/s with excess dumping, and local required fuel ratio20. Actual USITools version is1.0.0.0. Required fuel is a native throughput fraction; less than20 fuel reduces output and does not by itself prevent operation. The original paid power and agriculture package manifests allocate2 fuel per mapped Generator; the cultivation variant separately bills20. Those manifests and paid quantities remain unchanged.

The adapter only qualifies exact standalone Ranger hardware without swap bays, addons, specialist bonus, mass conversion, generated heat or modified efficiency. A real active native `PrepareRecipe` return supplies detached input/output/requirement vectors. The final `ResourceConverter.ProcessRecipe` call supplies the actual post-recipe efficiency argument. The reviewed fixed module must use multiplier1. Native stock skips that post-recipe multiplier when `_preCalculateEfficiency` is true; when false, it obtains `GetEfficiencyMultiplier`. The general converter demand bound follows that installed branch, rather than applying efficiency twice.

## Conservative output and ownership

The helper assumes maximum full-rate burn across the entire required reserve horizon, subtracts a small floating-point guard, and rates minimum future output from the remaining local fuel divided by the required20. This is a conservative conditional rating, not measured energy or additional production. It floors fuel endurance. Actual native stock/BRP remain the only fuel consumers and electricity producers.

Loaded stock requires positive current enabled local fuel, exact final recipe and fresh successful final native process observation. Unloaded stock requires an exact native BRP module identified by flight ID and persistent module ID, matching fuel/output proportions, productive constraint/rate, and one physical local Pull inventory owned by that proto tank. Any other converter sharing the same fuel path is rejected instead of granting the common fuel twice. Current saved efficiency is parsed as finite invariant numeric1; valid alternative lexical forms such as`1.0` are accepted.

The installed BRP0.2.7 `LastChangepoint` getter reads `Core.ResourceProcessor.lastUpdate`. `UpdateBackgroundState` passes current native UT to `UpdateState(UT,true)`. On normal successful catch-up, that method writes the physical proto snapshot amount and `OriginalAmount` from current inventory `Amount`, and advances `lastUpdate` to the supplied UT. The <=10-second freshness and exact saved/current inventory checks are therefore compatible with a normal long unloaded interval; the adapter requests native catch-up and does not rewrite processor fields.

## Initial paid activation and interruption

The existing construction activation child gains fixed Generator members/settings. Strict paid placement, complete exact member mapping, template hash, startup stock, escrow and anchored Foundation lineage precede the initial native call. A fresh initial intent requires each mapped Generator's positive reviewed billed fuel amount<=20, actual capacity20 and current positive stock within the paid allocation. The2-unit original packages are legitimate. No fill operation or free retrofit is performed.

Every required method/setting is preflighted before the first native mutation. Existing applying/complete/held next states and bytes are prepared before calling public `BaseConverter.StartResourceConverter`, bound to exact persistent module identity. The guarantee is accepted selected-save state preserved on the next normal save, not an fsync before the call. Actual activation flag/settings must read back exactly.

An already applied exact child returns before fresh fuel preflight. Later zero fuel and deliberate user shutdown remain utility outages; they never trigger another initial start. A retained applying/held child only observes unchanged paid hardware/settings. Fuel may legitimately have reached zero after a native start whose acknowledgement was lost. Only exact target readback completes that child; before/partial states remain held and native events are never repeated.

## Verification and remaining acceptance

Integrated checks: 24 new pure tests passed, including original manifest fuel allocations, full/worn 2- and 20-unit fuel, six-day lower output, exhaustion/roundoff, invalid quantities and exact one native multiplier. The focused generator/activation suite passed 29/29; main regression passed 434/434. WorldBridge compiles against installed KSP assemblies with 0 warnings/errors. Sixteen read-only Cecil contracts passed against the integrated build, including normal saved module identity, BRP timing/snapshot semantics, final requirements observation and the applied-child return before startup fuel preflight. These inspect code and conservation, not a running KSP world.

Root/placement still need actual paid commissioning, observed native activation/output below20 required fuel, saved persistent module identity, loaded-unpacked→anchored-packed→unloaded BRP catch-up, interruption/readback, depleted-fuel outage and deliberate user shutdown without restart. Existing thermal and continuous demand/input qualification still apply. No template receives native certification from these unit/metadata checks.
