import { test } from "node:test";
import assert from "node:assert/strict";
import { fileRejection, looksBinary, MaterialsClient, MaterialError, materialMessages, UploadQueue, referenceText } from "../../src/VoiceAssistant.Web/src/materials.js";

const document = { id: "a".repeat(32), title: "Notes", chunks: 2, updatedAt: "2026-10-01T12:00:00Z" };
const list = { documents: [document], usage: { documents: 1, chunks: 2 },
  limits: { maxDocuments: 300, maxChunks: 5000, maxFileBytes: 5242880, maxCharactersPerDocument: 400000 },
  extensions: [".md"] };
const tick = (): Promise<void> => new Promise(resolve => setImmediate(resolve));
test("Materials client obeys GET, single-file multipart, notes JSON and DELETE contract", async () => {
  const calls: { path: string; init?: RequestInit }[] = [];
  const client = new MaterialsClient(async (path, init) => {
    calls.push({ path, init });
    return init?.method === "DELETE" ? new Response(null, { status: 204 })
      : init?.method === "POST" ? Response.json({ ...document, characters: 10 }, { status: 201 }) : Response.json(list);
  });
  assert.deepEqual(await client.list(), list);
  await client.upload(new File(["text"], "notes.md"));
  const form = calls[1]!.init!.body as FormData;
  assert.deepEqual([...form.keys()], ["file"]);
  assert.equal((form.get("file") as File).name, "notes.md");
  assert.equal(calls[1]!.init!.headers, undefined);
  await client.notes(" Notes ", "Original text");
  assert.deepEqual(JSON.parse(calls[2]!.init!.body as string), { title: "Notes", text: "Original text" });
  await client.delete(document.id); assert.equal(calls[3]!.path, `/api/knowledge/${document.id}`);
  await assert.rejects(client.delete("../someone"), MaterialError);
});
for (const [status, code] of [[400, "invalid_request"], [413, "too_large"], [415, "unsupported_type"], [422, "no_text"],
  [409, "quota_exceeded"], [429, "busy"], [503, "knowledge_unavailable"], [401, "unauthorized"], [403, "forbidden"]] as const) {
  test(`Maps ${status}/${code} to safe English without echoing server details`, async () => {
    const client = new MaterialsClient(async () => Response.json({ error: code, message: "SECRET CONTENT" }, { status }));
    await assert.rejects(client.notes("Title", "Text"), error =>
      error instanceof MaterialError && error.message === materialMessages[code] && !error.message.includes("SECRET"));
  });
}
test("Unknown errors, network bodies, malformed success and expired auth are not success-shaped", async () => {
  await assert.rejects(new MaterialsClient(async () => new Response("<secret>driver</secret>", { status: 502 })).list(),
    error => error instanceof MaterialError && !error.message.includes("driver"));
  await assert.rejects(new MaterialsClient(async () => Response.json({ documents: [] })).list(), MaterialError);
  await assert.rejects(new MaterialsClient(async () => Response.json(document)).notes("T", "text"), MaterialError);
  await assert.rejects(new MaterialsClient(async () => { throw new Error("Sign-in expired"); }).list(),
    error => error instanceof MaterialError && error.code === "unauthorized");
});
test("Folder filtering handles case, separators, hidden/build directories, type and exact size limit", () => {
  for (const folder of ["node_modules", ".git", "bin", "obj", "dist", "build", ".venv", "__pycache__", ".hidden", "BIN"]) {
    assert.equal(fileRejection({ name: "a.ts", size: 1 }, `project\\${folder}\\a.ts`), "hidden/build folders");
  }
  assert.equal(fileRejection({ name: ".secrets.txt", size: 1 }), "hidden/build folders");
  for (const name of ["a.exe", "legacy.doc", "legacy.ppt", "picture.png", "archive.zip"]) assert.equal(fileRejection({ name, size: 1 }), "unsupported file types");
  assert.equal(fileRejection({ name: "REPORT.PDF", size: 5242880 }), null);
  assert.equal(fileRejection({ name: "report.pdf", size: 5242881 }), "over 5 MiB");
  assert.equal(fileRejection({ name: "file.ts", size: 100 }, "project/src/file.ts"), null);
});
test("Binary disguised as text and invalid UTF8 are filtered, known containers allowed", async () => {
  assert.equal(await looksBinary(new File([new Uint8Array([0, 1, 2])], "fake.txt")), true);
  assert.equal(await looksBinary(new File([new Uint8Array([255, 255])], "fake.md")), true);
  assert.equal(await looksBinary(new File(["Original 한글"], "notes.txt")), false);
  assert.equal(await looksBinary(new File([new Uint8Array([0, 1])], "slide.pptx")), false);
});
test("Two-slot queue never exceeds concurrency, retry only failures does not duplicate success", async () => {
  let active = 0, maximum = 0;
  const attempts = [0, 0, 0, 0], release: (() => void)[] = [];
  const queue = new UploadQueue(() => {});
  for (let i = 0; i < 4; i++) queue.add(`file${i}`, async () => {
    attempts[i]!++; active++; maximum = Math.max(maximum, active);
    await new Promise<void>(resolve => release.push(resolve)); active--;
    if (i === 1 && attempts[i] === 1) throw new MaterialError("busy");
  });
  await tick(); assert.equal(active, 2);
  release.shift()!(); release.shift()!(); await tick(); assert.equal(active, 2);
  release.shift()!(); release.shift()!(); await tick();
  assert.equal(maximum, 2); assert.equal(queue.items.filter(i => i.state === "failed").length, 1);
  queue.retryFailed(); await tick(); release.shift()!(); await tick();
  assert.deepEqual(attempts, [1, 2, 1, 1]); assert.ok(queue.items.every(i => i.state === "success"));
  queue.clearFinished(); assert.equal(queue.items.length, 0);
});
test("Cancel aborts active and queued items and never blindly retries cancelled uploads", async () => {
  const queue = new UploadQueue(() => {}); let started = 0;
  for (let i = 0; i < 3; i++) queue.add(`file${i}`, signal => new Promise((_resolve, reject) => {
    started++; signal.addEventListener("abort", () => reject(new DOMException("Cancelled", "AbortError")));
  }));
  await tick(); queue.cancel(); await tick();
  assert.equal(started, 2); assert.ok(queue.items.every(i => i.state === "cancelled"));
  queue.retryFailed(); await tick(); assert.equal(started, 2);
});
test("Queue bounded at300 entries and personal references never reveal placeholder host", () => {
  const queue = new UploadQueue(() => {});
  for (let i = 0; i < 300; i++) queue.add(String(i), () => new Promise(() => {}));
  assert.throws(() => queue.add("extra", async () => {}), /300/); queue.cancel();
  assert.equal(referenceText({ title: "<img>", url: `https://my-materials.invalid/${document.id}` }), "<img> (My meeting materials)");
  assert.match(referenceText({ title: "Guide", url: "https://example.org" }), /example.org/);
});
