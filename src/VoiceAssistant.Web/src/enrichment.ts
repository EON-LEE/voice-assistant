import type { CoachClient, CoachKind, Enrichment } from "./coach-client.js";

/** Each lane has one request; all newer turns invalidate both lanes even if fetch ignores abort. */
export class EnrichmentRequests {
  private generation = 0;
  private requests = new Map<CoachKind, AbortController>();
  constructor(private readonly client: Pick<CoachClient, "enrich">, private readonly changed: (kind: CoachKind, value: Enrichment | null) => void) {}
  reset(): void {
    this.generation++;
    for (const request of this.requests.values()) request.abort();
    this.requests.clear(); this.changed("question", null); this.changed("reply", null);
  }
  clear(kind: CoachKind): void {
    this.requests.get(kind)?.abort(); this.requests.delete(kind); this.changed(kind, null);
  }
  async load(kind: CoachKind, text: string): Promise<void> {
    this.clear(kind);
    if (!text.trim() || text.length > 600) return;
    const controller = new AbortController(), generation = this.generation;
    this.requests.set(kind, controller);
    try {
      const result = await this.client.enrich(kind, text, controller.signal);
      if (generation === this.generation && this.requests.get(kind) === controller && !controller.signal.aborted)
        this.changed(kind, result);
    } catch {
      // Enrichment is optional: never block or replace already displayed English on failure.
    } finally { if (this.requests.get(kind) === controller) this.requests.delete(kind); }
  }
}
export function renderEnrichment(korean: HTMLElement, pronunciation: HTMLElement | null, value: Enrichment | null): void {
  korean.textContent = value?.korean ?? ""; korean.hidden = !value;
  if (!pronunciation) return;
  pronunciation.replaceChildren(); pronunciation.hidden = !value?.pronunciation?.length;
  for (const chunk of value?.pronunciation ?? []) {
    const pair = document.createElement("span"); pair.className = "pronunciation-chunk";
    const en = document.createElement("span"), ko = document.createElement("span");
    en.className = "pronunciation-en"; ko.className = "pronunciation-ko"; ko.lang = "ko";
    en.textContent = chunk.en; ko.textContent = chunk.ko; pair.append(en, ko); pronunciation.append(pair);
  }
}
