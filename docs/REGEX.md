# Regex behavior

Pass a borrowed `IJqRegexEngine` directly to `ExecuteAsync`. The core package
contains the provider-neutral contracts and jq result handling. The application
owns the provider implementation and its dependencies.

Without an adapter, ordinary filters work and an executed regex operation reports
the catchable error `regex engine is not configured`. Unused regex branches stay
lazy. There is no implicit provider.

See the [application example](../examples/README.md) for explicit injection,
named captures and Unicode offsets.

The adapter receives the active cancellation token and borrowed shared memory
budget for compilation. Compiled handles receive the token on boolean and detailed
searches. The host owns the engine; Jq owns
compiled handles until all subject/result borrowers finish. Implementations must
cooperate inside their own operations, admit substantial internal storage before
allocation, and surface refusal as terminal `JqRegexResourceException`.

`TryCompile` receives immutable `ReadOnlyMemory<byte>` pattern bytes owned by Jq
through handle disposal. Cache lookups compare UTF-8 content and compile options;
the adapter compiles these bytes directly through the provider's native overload.

The optional [Utf8RegexAdapter](../examples/Utf8RegexAdapter.cs) source uses managed
`Lokad.Utf8Regex.Pcre2` 0.4.0 and its matching core package. It compiles into the
application, alongside explicit provider references. Other adapters choose their
syntax while preserving
complete-subject context, absolute offsets and the capture contract.

## Example adapter flags and syntax

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
`m`/`s`/`p` meanings differ from the mapping above.
Invalid syntax and unsupported constructs remain catchable jq language errors.

## Results and replacement filters

`test` uses the engine's boolean `IsMatch` primitive. `match`, `capture`, `scan`,
regex splitting and substitutions use detailed captures. jq result fields,
scalar-based offsets and lengths, unmatched capture nulls, and numbered capture
order are retained. Duplicate names fold in numbered order, last wins, including
nulls for non-participating groups.
`JqRegexCapture` carries only participation and absolute UTF-8 byte bounds,
including slot zero. Jq projects scalar positions lazily through one
sparse index per operation; overlapping captures and lookbehind reuse it.
Ranges must end on Unicode boundaries. ASCII prefixes need no index storage.

Detailed searches finish before their first result is emitted. `scan` and regex
splitting build their result strings directly from captures without retaining
match metadata. Whole-match scalar positions advance with the search.

After an empty global match Jq advances one Unicode scalar; an empty
match at the end is the last match. Detailed capture searches are separate
engine operations. `sub`/`gsub` evaluate jq replacement filters and align
their result streams.
Invalid-modifier diagnostics retain jq's implicit `g`: a prefix for `scan`, a
suffix for regex splitting and `gsub`.

## Limits and cancellation

The provider owns regex resource bounds. The example adapter uses Utf8Regex's
execution settings and default match timeout.

Providers own syntax validation and admission for compiled patterns, workspace
and capture storage. Detailed searches return actual captures with an optional
provider owner, disposed after consumption. Compiled handles outlive their active
results. Two idle compiled patterns are reused; completed one-use patterns are
retired. Native subject bytes stay borrowed and funded through matching. Jq admits
its copied cache keys, result values, lists and UTF-8 substitution builders.
Regex splitting advances through scalar
boundaries. Retained Jq buffers stay charged; every refusal terminates the command.
Reuse and structural ceilings are described in
[runtime memory rules](RUNTIME.md#memory-admission).

Provider resource exhaustion reports status 5 outside `try`/`?`. Syntax errors stay
catchable; cancellation propagates as `OperationCanceledException`. The example
adapter forwards the active token into eager compilation, boolean searches and
detailed matching. Utf8Regex 0.4.0 observes cancellation during its controlled
work. It exposes no shared-budget API, so provider storage remains outside the
host's shared accounting.
