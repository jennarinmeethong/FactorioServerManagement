import type { MapGeneration, ResourceSettings } from "../types";

const normal: ResourceSettings = { frequency: "normal", size: "normal", richness: "normal" };

/** Baseline values a preset is layered on top of — matches this app's own defaults, not raw Factorio internals. */
export const DEFAULT_MAP_GENERATION: MapGeneration = {
  width: 0, height: 0, water: "normal", startingArea: "normal", terrainSegmentation: "normal", peacefulMode: false,
  ironOre: { ...normal }, copperOre: { ...normal }, stone: { ...normal }, coal: { ...normal },
  uraniumOre: { ...normal }, crudeOil: { ...normal }, trees: { ...normal }, enemyBase: { ...normal },
  evolutionTime: 40, evolutionPollution: 60, evolutionDestroy: 30,
  enemyExpansionEnabled: true, enemyExpansionMinDistance: 3, enemyExpansionMaxDistance: 7,
  settlerGroupMinSize: 5, settlerGroupMaxSize: 20, expansionMinCooldown: 4, expansionMaxCooldown: 60,
  cliffElevationInterval: 40, cliffElevationOffset: 0, cliffRichness: "normal",
  technologyPriceMultiplier: 1, spoilTimeModifier: 1
};

export type MapPreset = { id: string; label: string; description: string; values: Partial<MapGeneration> };

/** Approximations of the presets documented at https://wiki.factorio.com/Map_generator, expressed on this app's level-based scale. */
export const MAP_PRESETS: MapPreset[] = [
  { id: "default", label: "Default", description: "ค่ามาตรฐานทุกอย่างอยู่ตรงกลาง เหมาะสำหรับเริ่มเกมทั่วไป", values: {} },
  { id: "rich-resources", label: "Rich Resources", description: "ทรัพยากรทุกชนิด (รวมฐานศัตรู) มีความอุดมสมบูรณ์สูงมาก", values: {
    ironOre: { ...normal, richness: "very-high" }, copperOre: { ...normal, richness: "very-high" }, stone: { ...normal, richness: "very-high" },
    coal: { ...normal, richness: "very-high" }, uraniumOre: { ...normal, richness: "very-high" }, crudeOil: { ...normal, richness: "very-high" },
    enemyBase: { ...normal, richness: "very-high" }
  } },
  { id: "marathon", label: "Marathon", description: "เทคโนโลยีแพงขึ้น 4 เท่า เหมาะกับเกมที่เล่นยาว", values: { technologyPriceMultiplier: 4 } },
  { id: "death-world", label: "Death World", description: "ฐานศัตรูใหญ่และถี่ขึ้นมาก วิวัฒนาการเร็วขึ้นตามเวลาแทนมลพิษ พื้นที่เริ่มต้นเล็กลง", values: {
    startingArea: "low", enemyBase: { frequency: "high", size: "high", richness: "normal" }, evolutionTime: 100, evolutionPollution: 20
  } },
  { id: "death-world-marathon", label: "Death World Marathon", description: "รวม Death World เข้ากับ Marathon — โหดและยาวพร้อมกัน", values: {
    startingArea: "low", enemyBase: { frequency: "high", size: "high", richness: "normal" }, evolutionTime: 100, evolutionPollution: 15, technologyPriceMultiplier: 4
  } },
  { id: "rail-world", label: "Rail World", description: "ทรัพยากรกระจายห่างกันแต่ก้อนใหญ่มาก เหมาะกับสร้างโครงข่ายรางรถไฟยาวๆ น้ำเยอะขึ้น", values: {
    water: "high", ironOre: { frequency: "low", size: "very-high", richness: "normal" }, copperOre: { frequency: "low", size: "very-high", richness: "normal" },
    stone: { frequency: "low", size: "very-high", richness: "normal" }, coal: { frequency: "low", size: "very-high", richness: "normal" },
    uraniumOre: { frequency: "low", size: "very-high", richness: "normal" }, crudeOil: { frequency: "low", size: "very-high", richness: "normal" }
  } },
  { id: "ribbon-world", label: "Ribbon World", description: "แผนที่เป็นแถบยาวไม่จำกัดความกว้าง จำกัดความสูงที่ 128 ทรัพยากรถี่แต่ก้อนเล็ก", values: {
    width: 0, height: 128,
    ironOre: { frequency: "very-high", size: "low", richness: "high" }, copperOre: { frequency: "very-high", size: "low", richness: "high" },
    stone: { frequency: "very-high", size: "low", richness: "high" }, coal: { frequency: "very-high", size: "low", richness: "high" },
    uraniumOre: { frequency: "very-high", size: "low", richness: "high" }, crudeOil: { frequency: "very-high", size: "low", richness: "high" }
  } },
  { id: "lakes", label: "Lakes", description: "น้ำเยอะเป็นทะเลสาบทั่วแผนที่ ต้นไม้น้อยลง", values: { water: "very-high", trees: { ...normal, frequency: "low" } } },
  { id: "island", label: "Island", description: "เริ่มต้นบนเกาะกลางทะเลกว้าง ต้นไม้น้อยลง", values: { water: "very-high", trees: { ...normal, frequency: "low" } } }
];

/** Resets to this app's defaults and layers the preset's deltas on top; keeps the current seed and any per-planet catalog overrides. */
export function applyPreset(current: MapGeneration, presetId: string): MapGeneration {
  const preset = MAP_PRESETS.find(item => item.id === presetId);
  if (!preset) return current;
  return { ...DEFAULT_MAP_GENERATION, seed: current.seed, controlOverrides: current.controlOverrides, ...preset.values };
}
