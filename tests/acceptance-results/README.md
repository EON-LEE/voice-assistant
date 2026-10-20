# Acceptance and latency records

Each JSON file is a dated record of one acceptance or latency run for the revision and Azure environment named in it.
The records are historical evidence, not the current release approval; current automated results come from CI (see
"Verification status" in the root README).

Identifiers inside these records, such as tenant, client or object IDs, registry and resource names, image references and
endpoints, belong to the environment that produced the run. They are kept unchanged as evidence and are already part of the
Git history. They are not configuration: never copy them into scripts, docs or parameters. Operators keep their own
environment in the git-ignored `infra/release.local.json` (see `infra/release.example.json`).

Only content-free, synthetic results belong here. Never add private scenarios, materials, audio, transcripts, logs or
evaluator outputs.
