import { computed, type ComputedRef } from 'vue'
import { useRoute } from 'vue-router'
import { useQuery, useQueryClient } from '@tanstack/vue-query'
import { fetchReport, reportKeys, type ReportIdentity } from '@/lib/api/reports'

/** Route params → stable report identity (IDs stay strings; see http.ts). */
export function useReportIdentity(): ComputedRef<ReportIdentity> {
  const route = useRoute()
  return computed(() => ({
    membershipTypeId: Number(route.params.membershipTypeId),
    membershipId: String(route.params.membershipId),
  }))
}

/**
 * The main report query. Multiple sections/views can call this freely;
 * TanStack Query dedupes on the key. `data === null` means "no report
 * generated yet", which is a real state rather than an error.
 */
export function useReportQuery(identity: ComputedRef<ReportIdentity>) {
  return useQuery({
    queryKey: computed(() => reportKeys.report(identity.value)),
    queryFn: ({ signal }) => fetchReport(identity.value, signal),
    staleTime: 5 * 60_000,
  })
}

export function useInvalidateReport(identity: ComputedRef<ReportIdentity>) {
  const client = useQueryClient()
  return () =>
    client.invalidateQueries({
      queryKey: reportKeys.report(identity.value),
    })
}
