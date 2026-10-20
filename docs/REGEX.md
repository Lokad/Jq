# Regex behavior

Pass a borrowed `IJqRegexEngine` directly to `ExecuteAsync`; the default
`JqUtf8RegexEngine.Instance` adapts managed `Lokad.Utf8Regex.Pcre2` 0.3.0
(transitively `Lokad.Utf8Regex` 0.3.0). The default adapter ships in the runtime
package. PCRE2-specific types stay behind the adapter.

The adapter receives the active cancellation token and borrowed shared memory
budget for compilation. Compiled handles receive the token on boolean and detailed
searches. The host owns the engine; Jq owns
compiled handles until all subject/result borrowers finish. Implementations must
cooperate inside their own operations, admit substantial internal storage before
allocation, and surface refusal as terminal `JqRegexResourceException`.

Default patterns go directly to the managed PCRE2 standard matcher; supported
syntax follows that engine. Custom adapters choose their syntax while preserving
complete-subject context, absolute offsets and the capture contract.

## Flags and syntax

| Flag | Behavior |
| --- | --- |
| `g` | Global matching; `gsub`, `scan` and regex splitting are global already. |
| `i` | PCRE2 caseless matching. |
| `m` | PCRE2 multiline anchors. |
| `s` | PCRE2 dot matches newlines. |
| `p` | Both multiline anchors and dot matching newlines. |
| `x` | PCRE2 extended syntax. |
| `n` | Reject empty matches through `NotEmpty`. |
| `l` | Unsupported; reported as a catchable language error. |

Unicode properties are enabled, newline convention is LF, and duplicate capture
names are accepted. The byte-splitting `\C` escape is forbidden to keep offsets
and extracted strings on Unicode boundaries. Neither jq's literal interpretation
of that escape nor its longest-match behavior is emulated. In particular, jq's
`m`/`s`/`p` meanings differ from the mapping above. Numeric capture definitions such as
`(?<1>a)` have no name-table entry in 0.3.0, so `capture` returns `{}`. Sparse
numeric definitions such as `(?<100>a)` are accepted but missing from detailed
results in that engine version.
Invalid syntax and unsupported constructs remain catchable jq language errors.

## Results and replacement filters

`test` uses the engine's boolean `IsMatch` primitive. `match`, `capture`, `scan`,
regex splitting and substitutions use detailed captures. jq result fields,
scalar-based offsets and lengths, unmatched capture nulls, and numbered capture
order are retained. Duplicate names fold in numbered order, last wins, including
nulls for non-participating groups.

After an empty global match Jq advances one Unicode scalar; an empty
match at the end is the last match. Detailed capture searches are separate
engine operations. `sub`/`gsub` evaluate jq replacement filters and align
their result streams.

## Limits and cancellation

The provider owns regex resource bounds. The default adapter uses Utf8Regex's
execution settings and default match timeout.

Providers own syntax validation and admission for compiled patterns, workspace
and capture storage. Detailed searches return actual captures with an optional
provider owner, disposed after consumption. Compiled handles outlive their active
results. Jq admits its own copied cache keys, encoded subjects, match/scan JSON
values, lists and substitution builders. Regex splitting advances through scalar
boundaries. Retained Jq buffers stay charged; every refusal terminates the command.
Reuse and structural ceilings are described in
[runtime memory rules](RUNTIME.md#memory-admission).

Provider resource exhaustion reports status 5 outside `try`/`?`. Syntax errors stay
catchable; cancellation propagates as `OperationCanceledException`. Utf8Regex
0.3.0 exposes neither shared-budget nor cancellation-token APIs. Its internal
storage is outside shared accounting, and the default adapter checks cancellation
before and after calls. It cannot interrupt a search in progress and may delay
cancellation arbitrarily on a pathological pattern. A cooperating provider can
admit its storage and observe cancellation during work.
