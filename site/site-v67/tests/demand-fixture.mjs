import {frameFixture} from './production-fixture.mjs';
export function demandFrame(){const f=frameFixture(),v=f.colony.vessels[0];v.crew=2;
 const common={sampleUt:100,processedEndUt:100,intervalGameSeconds:2,captureSequence:1,crewCount:2,timeFactor:1};
 v.lifeSupport={status:'partial',reason:'Synthetic passive USI observation',observedUt:100,supply:{...common,recyclerMultiplier:.5,grossSuppliesPerSecond:.001,grossMulchPerSecond:.001,configuredSuppliesPerSecond:.0005,configuredMulchPerSecond:.0005,suppliesConsumed:.001,mulchProduced:.0008},crewElectricity:{...common,configuredEcPerSecond:.02,electricityConsumed:.04}};
 v.powerAverage={windowId:1,windowStartUt:40,windowEndUt:100,windowGameSeconds:60,coveredGameSeconds:60,reportRealSeconds:60.1,ageRealSeconds:2,generationEc:60,consumptionEc:30,generationEcPerSecond:1,consumptionEcPerSecond:.5,status:'partial',basis:'supported-native-callbacks',reason:'Supported owners only'};return f;}
