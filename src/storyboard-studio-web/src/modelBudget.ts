import type { ModelTriangleBudget } from './types'

/**
 * How a model's runtime triangle budget is offered and read.
 *
 * The number belongs to the Reference Asset Compiler: it decides what a model
 * should cost from what the model is and how big it is. Everything here only
 * presents its answer -- or, when it has none, leaves the artist to choose --
 * and never fills a gap with a number of this studio's own.
 */

/** The bounds the compiler's reducer accepts for a number somebody types. */
export const MINIMUM_BUDGET = 1_000
export const MAXIMUM_BUDGET = 200_000

/** What the Auto choice can do for one model. */
export type AutoOffer =
  /** Still asking the compiler. */
  | { kind: 'loading' }
  /** The compiler chose a number below what the model has: Auto can prepare it. */
  | { kind: 'ready'; budget: number; text: string; reason: string | null }
  /** The model is already within the compiler's number, so Auto has nothing to reduce. */
  | { kind: 'within'; budget: number; text: string; reason: string | null }
  /** The compiler answered with no number: a character, which takes the rig route. */
  | { kind: 'no-number'; text: string; reason: string | null }
  /** The compiler could not answer at all: missing, too old, or refusing this model. */
  | { kind: 'unavailable'; text: string }

/**
 * What Auto offers, in the compiler's own words. `undefined` is still loading;
 * `null` is a question that could not be asked.
 */
export function autoOffer(budget: ModelTriangleBudget | null | undefined, triangles: number): AutoOffer {
  if (budget === undefined) return { kind: 'loading' }
  if (budget === null || budget.state !== 'Decided')
    return { kind: 'unavailable', text: budget?.detail ?? 'The compiler could not suggest a budget for this model.' }
  const reason = budget.roleReason
  if (budget.triangleBudget === null)
    return { kind: 'no-number', text: budget.summary ?? 'The compiler gives this model no reduction budget.', reason }
  const said = budget.summary ?? `About ${budget.triangleBudget.toLocaleString()} triangles.`
  const has = `This model has ${triangles.toLocaleString()}.`
  return budget.triangleBudget >= triangles
    ? { kind: 'within', budget: budget.triangleBudget, text: `${said} ${has} That is already within it, so Auto has nothing to reduce.`, reason }
    : { kind: 'ready', budget: budget.triangleBudget, text: `${said} ${has}`, reason }
}

/** Why a typed budget cannot be sent yet, or null when it can. */
export function manualBudgetProblem(raw: string, triangles: number): string | null {
  const text = raw.trim()
  if (text === '') return 'Enter a triangle budget.'
  const value = Number(text)
  if (!Number.isInteger(value)) return 'A budget is a whole number of triangles.'
  if (value < MINIMUM_BUDGET || value > MAXIMUM_BUDGET)
    return `A runtime budget is between ${MINIMUM_BUDGET.toLocaleString()} and ${MAXIMUM_BUDGET.toLocaleString()}.`
  if (value >= triangles)
    return `This model has ${triangles.toLocaleString()}. A derivative has to be smaller.`
  return null
}

/**
 * What Prepare for runtime sends: null for Auto, the artist's number, or
 * undefined while neither can run -- in which case Prepare stays disabled.
 */
export function preparationBudget(
  manual: boolean, offer: AutoOffer, raw: string, triangles: number,
): number | null | undefined {
  if (!manual) return offer.kind === 'ready' ? null : undefined
  return manualBudgetProblem(raw, triangles) === null ? Number(raw.trim()) : undefined
}

/**
 * The budget beside the triangle count in the Geometry panel, such as
 * "budget 30,000 (hero)", and whether the model is over it. Over is a fact
 * to notice, not an error: plenty of models are reviewed at full density.
 */
export function geometryBudget(
  budget: ModelTriangleBudget | null | undefined, triangles: number,
): { text: string; over: boolean; budget: number | null } | null {
  if (!budget || budget.state !== 'Decided') return null
  const role = budget.role ? ` (${budget.role})` : ''
  if (budget.triangleBudget === null) return { text: `no reduction budget${role}`, over: false, budget: null }
  return {
    text: `budget ${budget.triangleBudget.toLocaleString()}${role}`,
    over: triangles > budget.triangleBudget,
    budget: budget.triangleBudget,
  }
}
