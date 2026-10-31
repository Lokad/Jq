# Regex behavior

Pass a borrowed `IJqRegexEngine` to `ExecuteAsync`. The application owns its
implementation and dependencies; the core package has no implicit provider.
Without injection an executed regex operation reports the catchable error
`regex engine is not configured`. Unused branches remain lazy.
See the [application example](../examples/README.md).

## Adapter contract

`TryCompile` receives immutable UTF-8 pattern bytes owned by Jq through compiled
handle disposal, the shared memory budget and the active cancellation token.
Compiled handles expose actual numbered capture counts (excluding slot zero)
and immutable UTF-8 capture names. Providers own syntax validation, execution
bounds and admission for their programs, workspace and results.

`Prepare` borrows complete, immutable, already-funded subject bytes. The subject
provides boolean `IsMatch` and reusable cursors. Every operation and cursor advance
receives its current token; creation tokens must not govern later calls. Searches
preserve full-input anchors and lookbehind and report absolute UTF-8 byte bounds.

Cursor `Current` is borrowed until the next advance or disposal. `RetainCurrent`
returns independent ranges with an optional idempotent owner. Subjects, programs
and subject bytes outlive all their borrowers, including asynchronous suspension.
After an empty match, advance one Unicode scalar immediately; an empty match at
the end is final. Do not retry a nonempty alternative at the same position.

Jq admits its cache keys, result values and substitution builders. Two idle compiled
patterns are reusable. Refused provider admission throws `JqRegexResourceException`
with `IsMemoryAdmission = true` and terminates the command with status 5, outside
`try`/`?`. Other provider resource exhaustion is also terminal. Syntax/unsupported
errors return `false` from compilation. Cancellation propagates as
`OperationCanceledException`. See [memory admission](RUNTIME.md#memory-admission).

## jq results and modifiers

`test` uses the boolean primitive. Detailed operations construct jq results from
capture participation and byte ranges, projecting offsets and lengths to Unicode
scalars through a shared sparse index. Ranges must end on scalar boundaries.
Unmatched captures are null; duplicate names fold in numbered order, last wins.
`scan` and regex splitting construct strings without retaining match metadata.
Substitution cursors remain live across asynchronous jq replacement filters.

| Flag | Behavior |
| --- | --- |
| `g` | Global matching; `gsub`, `scan` and regex splitting are already global. |
| `i` | Caseless compilation. |
| `m` | Multiline anchors. |
| `s` | Dot includes newlines. |
| `p` | Multiline anchors and dot includes newlines. |
| `x` | Extended pattern syntax. |
| `n` | Suppress empty matches. |
| `l` | Unsupported; a catchable language error. |

The `m`/`s`/`p` meanings differ from upstream jq. Invalid-modifier diagnostics retain
jq's implicit `g`: prefixed for `scan`, appended for regex splitting and `gsub`.

## Qualification

Core contract tests cover ownership, admission and per-call cancellation separately
from real-engine semantic tests. The latter currently use a development-only PCRE2
0.4 bridge under `tools/RegexCompatibility/`, with CLR-to-UTF-8 name conversion,
per-search result allocation and pinned reflection for actual compiled capture
metadata. It enables Unicode properties, LF newlines and duplicate names, and forbids
byte-splitting `\C`. It preserves provider execution settings and cancellation.
Its provider storage lacks shared admission. It is not the application adapter.

Final producer-package qualification remains pending for native metadata, prepared
subjects/cursors, shared admission, borrowed-result behavior and current-call
cancellation. Semantic coverage through the temporary bridge does not qualify
those producer resource properties.
