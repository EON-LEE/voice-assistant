/** An error with a stable machine-readable code, so callers classify it without reading its (English) message. */
export class CodedError extends Error {
  constructor(readonly code: string, message: string) {
    super(message);
    this.name = "CodedError";
  }
}
