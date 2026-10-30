# ADR 0001 Public contract parsing and normalization

Date: 2026-10-29. Status: accepted for the 0.3 contract foundation. Scope: public app manifests and standalone connection policies; legacy credential profiles retain their existing semantics.

## Decisions

1. A public manifest is at most **48 KiB UTF-8**, including input whitespace. This reserves room beneath the 64 KiB control-frame ceiling; registration must additionally check the actual encoded enclosing frame. Standalone policy inputs are at most 8 KiB. Nesting is at most 16 object/array containers, root container depth 1. A resource cap of 10,000 parsed values applies within these size bounds.
2. Reject UTF-8 BOM, malformed UTF-8, non-JSON whitespace, comments, trailing content/commas, duplicate decoded object keys, nonfinite numbers and unpaired surrogate escapes. No replacement characters are introduced to rescue invalid encoding. A valid literal U+FFFD remains ordinary valid Unicode.
3. String limits count Unicode scalar values, not UTF-16 code units. Field names and identifiers are case sensitive. Text labels contain 1–64 scalars, cannot be all Unicode White_Space (including U+0085 and U+00A0), and reject C0/DEL controls; preserve other valid label content without Unicode normalization. Health-path whitespace uses this same Unicode White_Space property.
4. Contract integers must have JSON integer lexical form: reject a decimal point or exponent even when mathematically integral. Booleans and quoted numbers do not substitute for integer values. This rule applies to manifestVersion, ports, time budgets and expectedStatus values.
5. A policy requires mode. Missing connectTimeoutMs is 15000. Missing directWaitMs is min(mode default, connectTimeoutMs): fast-connect 1500, direct-first 8000, direct-only the full connectTimeoutMs. An explicitly greater wait is rejected rather than clamped. direct-only requires explicit allowRelay=false and equal total/wait; other modes default allowRelay=true. allowRelay=false under another mode remains valid and denies fallback.
6. Policy normalized output always includes mode, connectTimeoutMs, directWaitMs, allowRelay, prewarm (false), prewarmTimeoutMs (8000), prewarmIdleTimeoutMs (60000), relayBudgetBytes (null) and relayWarningBytes (null). Parenthesized values are defaults. relayBudgetAction is included only when a finite relayBudgetBytes requires explicit stop-relay; an action without a finite budget is rejected as meaningless.
7. relayBudgetBytes/relayWarningBytes are null or canonical uint64 decimal strings without leading zeros except 0. A finite hard budget requires relayBudgetAction=stop-relay. Warning must not exceed a finite hard budget. This validates declaration semantics, not user approval, quota execution or persistent accounting.
8. logicalAuthority is ASCII DNS host plus an optional canonical decimal port 1–65535, no userinfo, path, whitespace, trailing dot, IP literal or numeric address shorthand. Host labels are 1–63 ASCII letters/digits/hyphens, cannot start/end with a hyphen; total host length <=253. Normalize host letters to lowercase, preserve an explicit valid port. Exclude labels that make the entire host an IPv4/hex/numeric shorthand. Scheme is supplied by protocol, never embedded in authority.
9. Exports/imports each have at most 16 entries and distinct serviceId/alias within their array. At least one array is nonempty. protocol=https/wss requires authority; authority on other protocols, when supplied, still validates. localTarget is strictly 127.0.0.1 and an integer port 1024–65535.
10. An http health-check path starts with one slash, not two; length 1–1024 Unicode scalars, no whitespace, C0/DEL or fragment marker. expectedStatus contains 1–10 distinct lexical integers 100–599. A tcp check has only its type. No credentials/headers/methods can be added. Validation does not execute a probe or prove an arbitrary GET endpoint is harmless.
11. Normalized manifest preserves export/import ordering and only emits optional authority/healthCheck when present; it fills every import policy as above. JSON object key order is not significant in conformance comparison. No secret, device binding or grant is invented during normalization.
12. All input rejection is INVALID_REQUEST without input values in exception/error text. Parsing has no network, filesystem, process or credential side effects.

## Shared conformance format

`contracts/vectors/cases.json` has corpusVersion=1 and cases[]. Each case has id, kind (manifest/policy), inputUtf8Base64, accepted and either normalized or errorCode=INVALID_REQUEST. Base64 preserves malformed encodings and duplicate-key text. Fixtures are public synthetic declarations, not credentials.

Both implementations expose typed immutable values and a ToJson/toJson representation. Runners accept `--vectors PATH --output PATH`; result JSON has contractVersion=1, total, passed and cases[] containing id plus actual accepted and normalized/errorCode. They exit nonzero on mismatch and do not print rejected input. Output files are test artifacts, not imported application state.

## Consequences

The original 64 KiB manifest text and ambiguous default timeout wording are narrowed before public runtime adoption. Schema alone cannot enforce lexical integers, duplicates, cross-field budgets or scalar validity; runtime semantic checks are mandatory. Existing pairing Profile parsers remain untouched, so their older BOM/depth behavior is neither silently loosened nor accidentally broken.

This foundation is not a Broker/SDK implementation, an application permission check, FRP enforcement or a v0.3 release. Those remain separate tasks and acceptance gates.
