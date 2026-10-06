# Regex behavior

Lokad.Jq uses the published managed `Lokad.Utf8Regex.Pcre2` 0.3.0 package,
with `Lokad.Utf8Regex` 0.3.0 transitively. Patterns are passed directly to its
selected PCRE2 standard-matcher profile. Supported syntax and rejection of
unadmitted constructs follow that engine; this is not full PCRE2 or jq's
Oniguruma Perl NG compatibility. There is no native regex runtime or sibling
checkout dependency.

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
`m`/`s`/`p` meanings differ from the mapping above. Engine-admitted syntax, such
as `(?<1>a)`, is not additionally restricted to mimic PCRE.NET. In version
0.3.0 this defines numbered capture 1 without a name-table entry; `capture`
therefore returns `{}`. Sparse numeric definitions such as `(?<100>a)` are
also accepted but their capture is currently missing from the detailed result;
this engine limitation has been handed off for correction.
Invalid syntax and unsupported constructs remain catchable jq language errors.

## Results and replacement filters

`test` uses the engine's boolean `IsMatch` primitive. `match`, `capture`, `scan`,
regex splitting and substitutions use detailed captures. jq result fields,
scalar-based offsets and lengths, unmatched capture nulls, and numbered capture
order are retained. Duplicate names fold in numbered order, last wins, including
nulls for non-participating groups.

After an empty global match the adapter advances one Unicode scalar; an empty
match at the end is the last match. Detailed capture searches are separate
engine operations. `sub`/`gsub` still evaluate jq replacement filters and align
their result streams; they do not interpret native replacement templates.

## Limits and cancellation

`MaximumRegexWork` and `MaximumRegexTime` apply to each engine search rather
than accumulating across searches or input records. Defaults are 10,000,000
engine work steps and five seconds per search. The engine also receives depth
256 and 256 KiB working-memory limits. A global operation may use several
search allowances. Pattern length, subject buffers, capture capacity and
extracted values also consume jq's existing allocation/value/string budgets;
see [execution policy](EXECUTION_POLICY.md) for exact accounting limits.
These charges are conservative accounting, not a bound on total process memory
or an exact compiled-pattern size. Engine `MaxResultBytes` is reserved and is
not relied on for enforcement.

Work, time, depth and working-memory exhaustion terminate execution with status
5, bypassing `try` and `?`. Cancellation propagates as `OperationCanceledException`.
Published version 0.3.0 has no cancellation-token overloads: checks run before
and after compilation and each bounded synchronous search, and cannot interrupt
an engine call already in progress. Compilation is outside the matching timeout.
This differs from the previous PCRE.NET cumulative callback accounting.
