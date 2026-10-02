import { describe, expect, it } from "vitest";
import { completenessOf, costModel, energyModel, hasData, sumTotals } from "./reports";
import type { CostBreakdown, EnergyReport, EnergyTotals } from "./types";

const J_PER_KWH = 3_600_000;

const totals = (over: Partial<EnergyTotals> = {}): EnergyTotals => ({
  monitoredSeconds: 0,
  cpuPackageJoules: 0,
  cpuPackageSeconds: 0,
  gpuBoardJoules: 0,
  gpuBoardSeconds: 0,
  estimatedCompleteJoules: 0,
  estimatedCompleteSeconds: 0,
  estimatedPartialJoules: 0,
  estimatedPartialSeconds: 0,
  isEmpty: true,
  ...over,
});

const complete = (seconds: number, kwh: number) => totals({ monitoredSeconds: seconds, estimatedCompleteSeconds: seconds, estimatedCompleteJoules: kwh * J_PER_KWH, isEmpty: false });
const partial = (seconds: number, kwh: number) => totals({ monitoredSeconds: seconds, estimatedPartialSeconds: seconds, estimatedPartialJoules: kwh * J_PER_KWH, isEmpty: false });

const cost = (over: Partial<CostBreakdown> = {}): CostBreakdown => ({
  currency: "EUR",
  estimatedCost: null,
  estimatedCostRounded: null,
  pricedJoules: 0,
  unpricedJoules: 0,
  partialEstimateCost: null,
  pricesApplied: [],
  precision: "exact",
  ...over,
});

describe("energy model: Complete and Partial are never merged", () => {
  it("a purely complete period is complete", () => {
    expect(energyModel(complete(3600, 0.2)).kind).toBe("complete");
  });

  it("a purely partial period is partial -- its energy is a lower bound, not a complete figure", () => {
    const m = energyModel(partial(3600, 0.05));
    expect(m.kind).toBe("partial");
    expect(m.completeJoules).toBe(0);
    expect(m.partialJoules).toBe(0.05 * J_PER_KWH);
  });

  it("a mixed period keeps the two apart", () => {
    const mixed = totals({
      monitoredSeconds: 7200,
      estimatedCompleteSeconds: 3600,
      estimatedCompleteJoules: 0.2 * J_PER_KWH,
      estimatedPartialSeconds: 3600,
      estimatedPartialJoules: 0.05 * J_PER_KWH,
      isEmpty: false,
    });
    const m = energyModel(mixed);
    expect(m.kind).toBe("mixed");
    expect(m.completeJoules).toBe(0.2 * J_PER_KWH);
    expect(m.partialJoules).toBe(0.05 * J_PER_KWH);
  });

  it("a period with no estimate is 'none', never zero energy", () => {
    expect(energyModel(totals({ monitoredSeconds: 600, isEmpty: false })).kind).toBe("none");
    expect(energyModel(totals()).kind).toBe("none");
  });

  it("reports monitored time that had no usable estimate", () => {
    const m = energyModel(totals({ monitoredSeconds: 1000, estimatedCompleteSeconds: 600, estimatedCompleteJoules: 1, estimatedPartialSeconds: 100, estimatedPartialJoules: 1, isEmpty: false }));
    expect(m.uncoveredSeconds).toBe(300);
  });

  it("ignores floating-point summation noise but not real partial time", () => {
    expect(energyModel(totals({ monitoredSeconds: 10, estimatedCompleteSeconds: 10, estimatedCompleteJoules: 5, estimatedPartialSeconds: 1e-9, isEmpty: false })).kind).toBe("complete");
    expect(energyModel(totals({ monitoredSeconds: 10, estimatedCompleteSeconds: 9, estimatedCompleteJoules: 5, estimatedPartialSeconds: 1, estimatedPartialJoules: 1, isEmpty: false })).kind).toBe("mixed");
  });
});

describe("completeness mirrors the backend rule", () => {
  it("complete only if the complete estimate covers all monitored time", () => {
    expect(completenessOf(complete(100, 1))).toBe("complete");
    expect(completenessOf(partial(100, 1))).toBe("partial");
    expect(completenessOf(totals({ monitoredSeconds: 100, isEmpty: false }))).toBe("none");
    expect(completenessOf(totals({ monitoredSeconds: 100, estimatedCompleteSeconds: 99, estimatedCompleteJoules: 1, isEmpty: false }))).toBe("partial");
  });
});

describe("cost model: partial and unpriced energy are reported on their own", () => {
  it("shows an amount for priced complete energy", () => {
    const m = costModel(cost({ estimatedCost: 0.06, estimatedCostRounded: 0.06 }), complete(3600, 0.2));
    expect(m).toMatchObject({ kind: "amount", amount: 0.06, incomplete: false });
  });

  it("flags the amount as covering complete energy only when partial energy exists beside it", () => {
    const mixed = totals({ monitoredSeconds: 200, estimatedCompleteSeconds: 100, estimatedCompleteJoules: 1, estimatedPartialSeconds: 100, estimatedPartialJoules: 1, isEmpty: false });
    const m = costModel(cost({ estimatedCostRounded: 0.5, partialEstimateCost: 0.1 }), mixed);
    expect(m.kind).toBe("amount");
    expect(m.incomplete).toBe(true);
    expect(m.partialLowerBound).toBe(0.1); // reported separately, never added to the amount
    expect(m.amount).toBe(0.5);
  });

  it("flags the amount as incomplete when some energy was unpriced", () => {
    const m = costModel(cost({ estimatedCostRounded: 0.5, unpricedJoules: 100 }), complete(100, 1));
    expect(m.incomplete).toBe(true);
    expect(m.unpricedJoules).toBe(100);
  });

  it("a partial-only period has a lower-bound cost, not a cost", () => {
    const m = costModel(cost({ partialEstimateCost: 0.02 }), partial(100, 0.1));
    expect(m.kind).toBe("partialOnly");
    expect(m.amount).toBeNull();
    expect(m.partialLowerBound).toBe(0.02);
  });

  it("energy before any price existed is unpriced, not free", () => {
    const m = costModel(cost({ unpricedJoules: 5 * J_PER_KWH }), complete(100, 5));
    expect(m.kind).toBe("unpricedOnly");
    expect(m.amount).toBeNull();
  });

  it("no cost information at all is 'none', never zero", () => {
    expect(costModel(cost(), totals())).toMatchObject({ kind: "none", amount: null });
  });
});

describe("report data presence", () => {
  const report = (t: EnergyTotals): EnergyReport => ({
    title: "x",
    periodStartUtc: "",
    periodEndUtc: "",
    totals: t,
    gapCount: 0,
    gapDuration: "00:00:00",
    completeness: "none",
    cost: cost(),
    assumptions: [],
  });

  it("an empty period has no data", () => {
    expect(hasData(report(totals()))).toBe(false);
  });

  it("any monitored time counts as data", () => {
    expect(hasData(report(totals({ monitoredSeconds: 1, isEmpty: false })))).toBe(true);
  });
});

describe("summing rows never merges Complete into Partial", () => {
  it("adds each field to its own kind", () => {
    const sum = sumTotals([complete(100, 1), partial(50, 0.5), complete(10, 0.1)]);
    expect(sum.estimatedCompleteSeconds).toBe(110);
    expect(sum.estimatedCompleteJoules).toBeCloseTo(1.1 * J_PER_KWH, 3);
    expect(sum.estimatedPartialSeconds).toBe(50);
    expect(sum.estimatedPartialJoules).toBeCloseTo(0.5 * J_PER_KWH, 3);
    expect(sum.monitoredSeconds).toBe(160);
    expect(sum.isEmpty).toBe(false);
    expect(energyModel(sum).kind).toBe("mixed");
  });

  it("an empty list sums to an empty total", () => {
    expect(sumTotals([]).isEmpty).toBe(true);
  });
});
