import { apiRequest, getCsrfToken, newIdempotencyKey } from "./api";

export interface AdminRuntimeResponse {
  schemaVersion: number;
  observedAt: string;
  instance: {
    controlPlaneState: string;
    activeWorkerCount: number;
    webSocketConnectionCount: number;
    subscriptionCount: number;
  };
  workers: AdminWorker[];
  recentFailures: AdminFailure[];
}

export interface AdminWorker {
  session: {
    id: string;
    name: string;
    ownerUsername: string;
    gameId: string;
    gameName: string;
    state: string;
    stateVersion: number;
    lastActivityAt: string;
  };
  worker: {
    workerId: string | null;
    pid: number | null;
    workerEpoch: number;
    leaseStatus: string;
    heartbeatAt: string | null;
    heartbeatAgeMilliseconds: number | null;
    registered: boolean;
    ready: boolean;
    processExited: boolean;
    lastOutputSequence: number;
  };
  realtime: {
    hubState: string;
    snapshotSequence: number;
    snapshotBytes: number | null;
    snapshotSizeStatus: string;
    subscriptionCount: number;
    resyncCount: number;
    softOverflowCount: number;
    hardOverflowCount: number;
    faultCount: number;
    droppedPendingEventCount: number;
  };
  runtimeConsistency: string;
}

export interface AdminFailure {
  sessionId: string;
  sessionName: string;
  ownerUsername: string;
  gameId: string;
  gameName: string;
  workerEpoch: number;
  failedAt: string | null;
  reasonCode: string;
}

export async function getAdminRuntime(): Promise<AdminRuntimeResponse> {
  return apiRequest<AdminRuntimeResponse>("/admin/workers?recentFailureLimit=20");
}

export async function forceStopSession(sessionId: string, reason: string): Promise<void> {
  const csrf = await getCsrfToken();
  await apiRequest(`/admin/sessions/${encodeURIComponent(sessionId)}:force-stop`, {
    method: "POST",
    headers: {
      "Content-Type": "application/json",
      "X-CSRF-TOKEN": csrf,
      "Idempotency-Key": newIdempotencyKey(),
    },
    body: JSON.stringify({ reason }),
  });
}
