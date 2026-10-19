# Regex behavior

Jq's regex integration is internal: `JqRegexCache` compiles and owns patterns,
`JqRegexOptions` maps flags, and `JqMatch` builds jq results. The runtime directly
depends on managed `Lokad.Utf8Regex.Pcre2` 0.3.0 (and transitively
`Lokad.Utf8Regex` 0.3.0). It exposes no replaceable regex-engine interface or
separate adapter package.

Patterns go directly to the selected PCRE2 standard-matcher profile; supported
syntax follows that engine. There is no Oniguruma emulation or native runtime.

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

Each synchronous search has its own work/time allowance: defaults are 10,000,000
steps and five seconds, with depth 256 and a 256 KiB workspace bound. Compilation
is outside the search timeout. Global operations may consume multiple allowances.

Shared memory admission covers conservative pattern/workspace estimates, subject
buffers and capture storage. Cached patterns and retained buffers stay charged;
active borrowers delay refunds. Every refusal terminates the command. Reuse and
structural ceilings are documented in [execution policy](EXECUTION_POLICY.md).

Work, time, depth and workspace exhaustion report status 5 outside `try`/`?`.
Cancellation propagates separately. Version 0.3.0 has no cancellation-token API;
checks surround compilation and each bounded search, so an active engine call
cannot be interrupted. `MaxResultBytes` is reserved and is not an enforced guard.
