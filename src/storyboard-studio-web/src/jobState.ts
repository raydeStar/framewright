import type { JobSummary } from './types'

/**
 * A retry replaces the user-facing status of the attempt it follows. Keep the
 * earlier attempt in history for auditability, but do not continue presenting
 * it as the current failure after a newer attempt exists.
 */
export function getSupersededJobIds(jobs: JobSummary[]): Set<string> {
  return new Set(jobs.flatMap(job => job.retryOfJobId ? [job.retryOfJobId] : []))
}
