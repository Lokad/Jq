# Regex behavior

`JqExecutionPolicy.RegexEngine` selects a borrowed `IJqRegexEngine`; the default
`JqUtf8RegexEngine.Instance` adapts managed `Lokad.Utf8Regex.Pcre2` 0.3.0
(transitively `Lokad.Utf8Regex` 0.3.0). The default adapter ships in the runtime
package. PCRE2-specific types stay behind it; there is no implicit fallback.

The adapter receives the active cancellation token and borrowed shared memory
budget for compilation. Compiled handles receive the token on boolean and detailed
searches and retain their budget association. The host owns the engine; Jq owns
compiled handles until all subject/result borrowers finish. Implementations must
cooperate inside their own operations, admit substantial internal storage before
allocation, and surface refusal as terminal `JqRegexResourceException`.

Default patterns go directly to the PCRE2 standard matcher; supported syntax
follows that engine. There is no Oniguruma emulation or native runtime. Custom
adapters explicitly choose their syntax while preserving complete-subject context,
absolute offsets and the capture contract.

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
engine operations. `sub`/`gsub` still evaluate jq replacement filters and align
their result streams; they do not interpret native replacement templates.

## Limits and cancellation

Jq imposes no regex work allowance or search deadline. The default adapter uses
unlimited engine work and an infinite timeout, retaining depth 256, a 256 KiB
workspace bound and a 16,384-character pattern guard. The latter bounds
constructor exposure; it does not guarantee compile-depth safety or cancellation.

Shared admission covers conservative pattern/workspace estimates, subject buffers
and capture storage. Eager match/scan results, named-capture DOMs, list capacity
and substitution builders preflight construction estimates. Regex splitting
advances through scalar boundaries without an input-sized offset table.
Jq owns copied keys/cache metadata; the provider owns its compiled representation.
Each allocation has one owner; cached handles and retained
buffers stay charged, and active borrowers delay refunds. Every refusal terminates
the command. Reuse and structural ceilings are described in
[execution policy](EXECUTION_POLICY.md).

Depth/workspace exhaustion reports status 5 outside `try`/`?`. Syntax errors stay
catchable; cancellation propagates as `OperationCanceledException`. Utf8Regex
0.3.0 has no cancellation-token API: the default adapter checks before and after
engine calls and cannot interrupt a search in progress. It may therefore delay
cancellation arbitrarily on a pathological pattern. A cooperating adapter can
observe cancellation during work; Jq supplies no timer wrapper or abandoned task.
The default engine's `MaxResultBytes` is reserved, not an enforced guard.
