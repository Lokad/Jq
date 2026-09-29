# Compatibility matrix

Target profile: jq 1.8.2, common release build capabilities (decimal literals,
regex support). This matrix is the committed inventory for language, builtins,
and command behavior. Statuses are explicit; skipped or missing behavior is
never counted as supported.

Normative public references use the jq 1.8.2 manual sections, the
`jqlang/jq` command parser and builtin registries (`src/main.c`,
`src/builtin.c`, `src/builtin.jq`, `src/libm.h`, `src/parser.y`), and the
`jqlang/jq` test families (`tests/jq.test`, `tests/man.test`,
`tests/shtest`, `tests/onig.test`, `tests/optional.test`,
`tests/base64.test`, `tests/uri.test`). No inspection checkout is required
to read this file.

## Statuses

- `unimplemented`: no usable behavior yet.
- `partial`: some overloads or cases work; caveats list what is missing.
- `implemented`: behavior exists but differential evidence is still narrow.
- `verified`: behavior exists with focused tests and, where applicable,
  opt-in reference comparison.
- `intentionally different`: embedding policy differs on purpose, with reason.

## How to read test IDs

- `JqTests.*`: deterministic public-API cases in `tests/Lokad.Jq.Tests`.
- `HostBoundaryTests.*`: descriptor ownership and cleanup cases.
- `JqCompatibilityProbeTests.*`: golden-case schema and opt-in reference probe
  introduced for I01. Fixed local cases always run; reference cases skip
  unless `LOKAD_JQ_REFERENCE_JQ` points at an independently installed
  executable.

## Command options

| Flag | Normative reference | Status | Tests | Caveats | Host | Evidence |
| --- | --- | --- | --- | --- | --- | --- |
| `-n`, `--null-input` | Manual: Invoking jq; command parser | implemented | JqTests, Probe identity/null cases | Single null input; interaction with `input`/`inputs` not yet implemented | none | Release suite 309 passing |
| `-R`, `--raw-input` | Manual: Invoking jq | partial | JqTests raw/slurp cases | LF splits, CR is data; CR/LF edge cases and `--seq` interactions open | stdin bytes | Existing raw tests pass |
| `-s`, `--slurp` | Manual: Invoking jq | partial | JqTests raw slurp | Slurp aggregates buffered input; incremental cursor (I17) not done | stdin/file bytes | Existing slurp tests pass |
| `-c`, `--compact-output` | Manual: Invoking jq | implemented | JqTests compact output | Default is compact; pretty-print selection order handled in parser | stdout bytes | Existing output tests pass |
| `-r`, `--raw-output` | Manual: Invoking jq | implemented | JqTests raw output | Strings printed without JSON quotes | stdout bytes | Existing tests pass |
| `-j`, `--join-output` | Manual: Invoking jq | implemented | Manual inspection | No newline after each output | stdout bytes | Covered by invocation parsing; needs byte tests in I19 |
| `-a`, `--ascii-output` | Manual: Invoking jq | implemented | Existing format paths | ASCII escaping via encoder | stdout bytes | Covered by serializer paths |
| `--tab` | Manual: Invoking jq | implemented | JqTests indent case | Tab indentation | stdout bytes | Inline indent test passes |
| `--indent n` | Manual: Invoking jq | partial | JqTests indent case | Accepts 0-8 in this implementation; upstream documents -1 to 7; reconcile in I19 | stdout bytes | Needs parity decision and tests |
| `--unbuffered` | Manual: Invoking jq | intentionally different | JqTests unbuffered case | Accepted as no-op; hosted `AppendWhileOpenAsync` already models backpressure; no implicit flush | stdout backpressure | Test passes; flushing capability tracked in I19 |
| `-f`, `--from-file` | Manual: Invoking jq | implemented | JqTests filter-file cases | Filter file read through host; NUL-byte program check open | virtual file read | Tests pass; close-retry covered |
| `--arg name value` | Manual: Invoking jq | implemented | JqTests null-input/arg cases | String variable; `$ARGS.named` populated | none | Tests pass |
| `--argjson name value` | Manual: Invoking jq | implemented | JqTests argjson case | JSON variable; invalid JSON is exit 2 | none | Tests pass |
| `--args`, `--jsonargs` | Manual: Invoking jq | implemented | JqTests args case | Remaining args become positional strings/JSON | none | Tests pass |
| `-V`, `--version` | Manual: Invoking jq | intentionally different | JqTests version case | Prints `Lokad jq`; never claims upstream `jq-1.8.2` identity | none | Test passes |
| `--build-configuration` | Manual: Invoking jq | intentionally different | No focused test yet | Prints `Lokad jq`; truthful local configuration pending I19 | none | Needs help/version/configuration tests |
| `--` | Manual: Invoking jq | partial | No focused test yet | Terminates special-argument processing; full file/program/negative-filter rules pending I19 | none | Needs I19 tests |
| `--raw-output0` | Manual: Invoking jq | unimplemented | — | Implies `-r`, NUL terminator, rejects NUL-containing strings | stdout bytes | Returns unsupported-option today |
| `-S`, `--sort-keys` | Manual: Invoking jq | unimplemented | — | Sorted object keys on output | stdout bytes | Returns unsupported-option today |
| `-C`, `--color-output` | Manual: Invoking jq | unimplemented | — | Colorized output; terminal profile must come from host | host terminal profile | Returns unsupported-option today |
| `-M`, `--monochrome-output` | Manual: Invoking jq | unimplemented | — | Disables color | host terminal profile | Returns unsupported-option today |
| `--seq` | Manual: Invoking jq; Streaming section | unimplemented | — | application/json-seq framing | stdin/stdout bytes | Returns unsupported-option today |
| `--stream`, `--stream-errors` | Manual: Streaming | unimplemented | JqTests unsupported-option cases | Streaming parse and event encoding | stdin/stdout bytes | Rejected today; I18 owns design |
| `-L`, `--library-path dir` | Manual: Modules | unimplemented | — | Module search path; hosted resolution | virtual file search | Returns unsupported-option today |
| `--slurpfile name file` | Manual: Invoking jq | unimplemented | — | Array variable from JSON file | virtual file read | Not parsed today |
| `--rawfile name file` | Manual: Invoking jq | unimplemented | — | String variable from file | virtual file read | Not parsed today |
| `-e`, `--exit-status` | Manual: Invoking jq | unimplemented | — | Exit code from last output values | stdout/status | Not parsed today |
| `-b`, `--binary` | Manual: Invoking jq (Windows) | unimplemented | — | Binary stream mode; platform-scoped | host streams | Not parsed today |
| `-h`, `--help` | Manual: Invoking jq | unimplemented | — | Truthful help text pending I19 | none | Not parsed today |
| `--run-tests`, `--debug-dump-disasm`, `--debug-trace`, `--debug-trace=all` | Command parser (tool switches) | unimplemented | — | Inventory explicitly; implement only what belongs in embedding vs dev tool; never silently ignore | none | Returns unsupported-option today |

## Grammar and evaluation

| Feature | Normative reference | Status | Tests | Caveats | Host | Evidence |
| --- | --- | --- | --- | --- | --- | --- |
| Literals: `null`, `true`, `false`, numbers, strings | Manual: Basic filters | implemented | JqTests basic/literal cases | Signs stay operators (`1+2`, `1 - -2` compute); leading-dot fractions lex; exponents need digits; profile in docs/NUMERIC_PROFILE.md | none | Core literal tests pass |
| Identity `.`, field `.foo`, `.["foo"]`, index `.[0]`, slice `.[1:3]`, iterator `.[]`, optional `?`, descent `..` | Manual: Basic filters | implemented | JqTests index/slice/optional/descent cases | Quoted fields, recursive pre-order descent, and suppress-to-empty optionals; contiguous `?//` is the binding-alternation token (spaced `? //` stays postfix suppression plus `//`) | none | Core tests pass |
| Comma `,`, pipe `\|`, precedence, parentheses | Manual: Basic filters | implemented | JqTests comma/pipe/product cases | Lazy streams; comma is sequential, pipe threads outputs | none | Generator tests pass |
| Object `{a:1}`, `{a:(1,2)}`, array `[f]` construction | Manual: Basic filters | implemented | JqTests object-product/key/shorthand cases | Lazy first-key-major products; dynamic and interpolated keys with validated string keys; empty branches yield nothing; `{$x}` and `{$x: v}` shorthands read bound variables (constant non-string keys fail at compile time) | none | Core key tests pass |
| Arithmetic `+ - * / %`, comparisons, boolean `and`/`or`/`not`, alternative `//` | Manual: Conditionals and Comparisons; Builtin operators | partial | JqTests product/operator/`//` cases | Full type matrix (subtract, recursive merge, symmetric repeat, split division, narrowed coercions); `//` filters goods with right associativity; comparisons do not chain; `and`/`or` short-circuit per left value over generator streams | none | Core operator tests pass |
| String interpolation `"\(f)"`, `@format "text \(f)"` | Manual: Basic filters; String interpolation | partial | JqTests interpolation/nesting cases | Nesting-aware lexing and splitting (nested quotes); literal backslash-paren stays a known edge; full reparse-timing audit still open | none | Core nesting test passes |
| `if/then/elif/else/end` | Manual: Conditionals | implemented | JqTests.Conditionals | Keyword-shaped field names preserved (`.else`, `1.and true`) | none | Conditional tests pass |
| `as $x`, destructuring, `$ARGS`, lexical scope | Manual: Advanced features; Assignment | implemented | JqTests.Bindings | Body runs against the outer input while the source streams per input; array/object patterns bind missing fields as null and reject mistyped containers; `?//` takes the first matching alternative with unmatched names pre-initialized to null and retries later alternatives on catchable body errors; `{$x: PAT}` binds the field and matches the inner pattern with inner shadowing; bindings are lexically scoped with invocation variables visible inside | none | Binding, alternation, shorthand, and scope tests pass |
| `def f: ...`, `def f(a;b): ...`, closures, recursion | Manual: Advanced features | implemented | JqTests.Functions | Value params stream once per call, filter params re-evaluate per use with call-site bindings; closures capture definition environments (redefinition keeps older closures on prior bindings); unknown names and out-of-scope variables fail at compile time; self-calls in abandonment-free tail positions (body, if branches, comma/`//` right sides, `?`, nested definition bodies, single-combination final pipe values) loop on a heap worklist while other positions stay depth-bounded; tail calls under binding sources and collected positions remain bounded by the evaluation guard; multi-valued argument combinations use last-argument-outer order (upstream range/3 vectors show first-argument-outer; tracked for a cartesian-order increment) | none | Definition, closure, arity, backtracking, factorial, tail-run, and quota tests pass, including byte-exact upstream vectors |
| `try/catch`, `?` suppression, `error`, `halt`, `halt_error`, `label`/`break` | Manual: Advanced features; I/O | implemented | JqTests.Control | Tight `try ALTERNATIVE (catch ALTERNATIVE)?`; handlers observe payloads (explicit error values unwrapped, other failures as message strings) with the payload as input; `?` and bare `try` suppress; `error` (bare or messaged, first value wins) renders uncaught as `error: {payload}`; `halt` exits 0 silently while `halt_error(code?)` exits with the code and renders the input raw/JSON/nothing; `label`/`break` are lexically scoped with dynamic unwinding and keep prior outputs; quota, compile, cancellation, host, halt, break, and tail-call signals always propagate (quota/cancellation under `try` proven); `and`/`or` short-circuit per left value over generator streams | stderr/status | Control tests pass, including upstream try/label/halt vectors |
| Path expressions, `=`, `\|=`, `//=`, arithmetic updates | Manual: Assignment | implemented | JqTests.Paths | Paths enumerate once from the original input; `=` folds values outermost per path while `\|=` threads evolving state through first-only updates with empty-update deletion; `//=` and arithmetic updates evaluate the right side against the original input per value; updates bind tighter than `//` and never chain; spine-rebuilding updates never alias sibling outputs; intactness uses value equality rather than reference identity | none | Assignment, isolation, and failure tests pass, including upstream vectors |
| `reduce`/`foreach`, `limit`, `first`, `last`, `nth`, `isempty`, `while`, `until`, `repeat`, `recurse`, `walk` | Manual: Advanced features | implemented | JqTests.Reduction, JqTests.Iteration | Sources are update-level with full binding patterns (initializers outside scope); accumulators stream per initializer, item, state, and update output with budget charges; `foreach` yields intermediates or per-update extractions; `limit` pulls at most ceil(n) with upstream negative-count errors; `nth` truncates with upstream negative-index errors; `isempty` pulls at most once; `while`/`until`/`recurse` run depth-first on explicit stacks; `repeat` re-evaluates per round as a constant stream; `walk` rebuilds bottom-up with first-only children; consumers stop producers through lazy pulls | none | Reduction and iteration tests pass, including upstream while/until/repeat vectors |
| `import`/`include`, `module`, `modulemeta`, `$__loc__` | Manual: Modules | unimplemented | — | Hosted search and caching open (I16) | virtual module reads | Missing |
| Comments `#`, multiline, string escapes | Manual: Comments; Basic filters | implemented | JqTests comment/layout/escape cases | Comments run to line end; strict JSON escapes with spans; unterminated strings explicit | none | Core lexer tests pass |

## Values, ordering, and serialization

| Feature | Normative reference | Status | Tests | Caveats | Host | Evidence |
| --- | --- | --- | --- | --- | --- | --- |
| null vs missing vs empty stream; truthiness | Manual: Types and Values | implemented | JqTests null/empty cases | C# null is JSON null; zero outputs is empty; missing fields read as null | none | Null/empty theory passes |
| Equality and total ordering | Manual: Conditionals and Comparisons; sort order | implemented | JqTests equality/ordering/min-max cases | Kind-aware equality; total order null, false, true, numbers, scalar strings, lexical arrays, sorted-key objects; NaN unequal, sorts as null | none | Ordering/equality theories pass |
| Number literals, precision, signed zero, non-finite | Manual: Types and Values; docs/NUMERIC_PROFILE.md | partial | JqTests double-profile/non-finite/division cases | Double domain with integral storage; deliberate decNumber divergences (literal text, >2^53 comparison, exponent case) recorded in the profile | none | Profile pins pass; math builtins pending I14 |
| Object key order, duplicate keys | Manual: Types and Values | implemented | JqTests duplicate-key ingress cases | Insertion order kept; duplicates last-wins at first position with escapes decoded; equality order-insensitive | none | All four ingress paths pass |
| Unicode scalar indexing, `length`, slices | Manual: Types and Values | partial | Slice/length cases | `length`/slices count runes; search uses UTF-16 offsets; audit in I03/I12 | none | Needs offset tests |
| Compact vs pretty rendering, LF bytes, sorted keys, ASCII | Manual: Invoking jq | partial | Compact/indent/format cases | Compact default; pretty uses platform newlines; sorted/color pending | stdout bytes | Needs I19 byte tests |

## Structural and collection builtins

| Name/arity | Normative reference | Status | Tests | Caveats | Host | Evidence |
| --- | --- | --- | --- | --- | --- | --- |
| `length/0`, `type/0` | Manual: Builtin operators | implemented | JqTests basic/format cases | `length` on string counts runes, null is 0 | none | Passing |
| `keys/0`, `keys_unsorted/0` | Manual: Builtin operators | unimplemented | — | Missing; the new builtin registry is the single source for implemented names and will extend in I11 | none | Unsupported function today |
| `has/1`, `in/1`, `inside/1` (via `IN`/`inside`) | Manual: Builtin operators | partial | `contains`/`inside` narrow paths | `contains`/`inside` exist with limited semantics; `has`/`in`/`IN` missing | none | Needs I11 |
| `contains/1`, `inside/1` | Manual: Builtin operators | partial | JqTests via functions | String/array/object containment simplified; heterogeneous cases open | none | Narrow cases pass |
| `map/1`, `map_values/1` | Manual: Builtin operators (`builtin.jq`) | unimplemented | — | Missing; must use generator primitives, not eager shortcuts | none | Unsupported today |
| `select/1`, `empty/0`, `not/0` | Manual: Builtin operators | implemented | JqTests.Select | `select` streams without buffering; `empty` yields nothing | none | Select tests pass |
| `paths/0..1`, `path/1`, `getpath/1`, `setpath/2`, `delpaths/1`, `del/1`, `pick/1` | Manual: Builtin operators; Assignment | partial | JqTests.Paths | `path` (with `def`/call/binding/descent traversal), `getpath` (including path-mode synthesis), `setpath` (null creation, negative resolution, null padding with an explicit too-large bound, slice splice), `delpaths` (sorted-group deletion), `del` (path expressions), and `pick` exist; `paths/0..1` exists over descent pairs with optional filtering; slice path components render as start/end objects (best effort) | none | Path builtin tests pass with upstream vectors |
| `to_entries/0`, `from_entries/0`, `with_entries/1` | Manual: Builtin operators (`builtin.jq`) | unimplemented | — | Missing | none | Unsupported today |
| `add/0`, `add/1`, `flatten/0..1`, `reverse/0` | Manual: Builtin operators | partial | JqTests add/flatten-adjacent cases | `add`/`flatten`/`reverse` exist; depth arg and empty-input semantics need I11 tests | none | Narrow cases pass |
| `sort/0`, `sort_by/1`, `group_by/1`, `unique/0`, `unique_by/1`, `min/0`, `max/0`, `min_by/1`, `max_by/1`, `bsearch/1` | Manual: Builtin operators | partial | min/max narrow cases | Only `min`/`max` exist with simplified compare; `_by` variants and mixed-type sort open | none | Needs I11 |
| `range/0..3` | Manual: Builtin operators (`builtin.jq` + core) | partial | JqTests range-arity error | 1-3 args supported; 0-arg errors; negative/zero steps and generator laziness need I10/I11 | none | Arity error test passes |
| `transpose/0`, `combinations/0..1` | Manual: Builtin operators (`builtin.jq`) | unimplemented | — | Missing | none | Unsupported today |
| `any/0..2`, `all/0..2` | Manual: Builtin operators | partial | Narrow array `any`/`all` | Array-only; generator `any(gen;cond)`/`all(gen;cond)` and short-circuit reads open | none | Needs I11 |
| `first/0..1`, `last/0..1`, `nth/0..1`, `isempty/1`, `limit/2`, `while/2`, `until/2`, `repeat/1`, `recurse/0..2`, `walk/1` | Manual: Advanced features | unimplemented | — | Missing (I10) | none | Unsupported today |
| `indices/1`, `index/1`, `rindex/1` | Manual: Builtin operators | partial | Narrow indices paths | `indices`/`index` exist; `rindex` missing; string offsets need scalar audit | none | Narrow cases pass |
| `tojson/0`, `fromjson/0`, `tonumber/0`, `toboolean/0`, `tostring/0` | Manual: Builtin operators | implemented | JqTests conversion paths | Narrow semantics; invalid-input diagnostics need I12 review | none | Passing |

## Strings, encodings, and formats

| Name/arity | Normative reference | Status | Tests | Caveats | Host | Evidence |
| --- | --- | --- | --- | --- | --- | --- |
| `utf8bytelength/0`, `explode/0`, `implode/0` | Manual: Builtin operators | partial | `explode`/`implode` narrow cases | `explode`/`implode` exist; `utf8bytelength` missing; surrogate/NUL audit open | stdin/stdout bytes | Needs I12 |
| `split/1` (literal) | Manual: Builtin operators | partial | Narrow split/join | Literal split exists; regex `split/2`/`splits` missing | none | Narrow cases pass |
| `join/1` | Manual: Builtin operators (`builtin.jq` + core) | partial | Narrow join | Exists with simplified null handling; full `join($x)` parity open | none | Narrow cases pass |
| `startswith/1`, `endswith/1`, `ltrimstr/1`, `rtrimstr/1`, `trim/0`, `ltrim/0`, `rtrim/0` | Manual: Builtin operators | implemented | JqTests trim/prefix cases | Implemented; ASCII vs Unicode trimming audit in I12 | none | Passing |
| `ascii_downcase/0`, `ascii_upcase/0` | Manual: Builtin operators (`builtin.jq` specifies ASCII-only) | intentionally different | Narrow case-conversion paths | Currently Unicode-wide `ToLowerInvariant`/`ToUpperInvariant`; must become ASCII-only in I12 | none | Divergence recorded; needs fix |
| `@text`, `@json`, `@html`, `@uri`, `@csv`, `@tsv`, `@sh`, `@base64`, `@base64d` | Manual: String interpolation; `builtin.c` formats | partial | JqTests.Formatting | Core formats exist; `@urid`, invalid UTF-8, unpaired surrogates, percent/base64 edge cases open | stdout bytes | Format tests pass |
| Formatted interpolation `@fmt "\(f)"` | Manual: String interpolation | partial | JqTests format-template cases | Literal segments vs interpolated values partly covered; exact-bytes audit in I12 | stdout bytes | Narrow tests pass |

## Regex

| Name/arity | Normative reference | Status | Tests | Caveats | Host | Evidence |
| --- | --- | --- | --- | --- | --- | --- |
| `test/1..2` | Manual: Regular expressions (`builtin.jq` + `_match_impl`) | partial | JqTests.Test | Flag mapping and multi-result operands simplified; Oniguruma parity open (I13) | regex engine | Narrow tests pass |
| `gsub/2..3` | Manual: Regular expressions | partial | JqTests.Gsub | Replacement-stream alignment implemented; global/flag/empty-match semantics need I13 | regex engine | Gsub tests pass |
| `match/1..2`, `capture/1..2`, `scan/1..2`, `splits/1..2`, `split/2`, `sub/2..3` | Manual: Regular expressions | unimplemented | — | Missing overloads; shared cache/work accounting must be reused | regex engine | Unsupported today |
| Flags, captures, scalar offsets, advanced syntax | Manual: Regular expressions; Oniguruma docs | partial | Gsub flag/offset cases | PCRE.NET with UTF/UCP, LF newlines, `NeverBackslashC`; full Oniguruma matrix open | regex engine | Needs I13 matrix |

## Math

| Name/arity | Normative reference | Status | Tests | Caveats | Host | Evidence |
| --- | --- | --- | --- | --- | --- | --- |
| `abs/0`, `floor/0`, `sqrt/0` | Manual: Math (`builtin.jq` + `libm.h`) | partial | Narrow abs/floor/sqrt paths | Exist with double semantics; domain/negative-zero/non-finite audit open | none | Narrow cases pass |
| `ceil`, `round`, `trunc`, `nearbyint`, `rint`, `remainder`, `fmod`, `pow`, `exp`, `log`, trigonometric/hyperbolic, `tgamma`/`lgamma`, `modf`/`frexp`, classification (`isinfinite`, `isnan`, `isnormal`, `infinite`, `nan`), `have_decnum` | Manual: Math; `libm.h` capability matrix | unimplemented | — | Full registry and arities pending I14; platform/libm differences must be recorded as profile issues | none | Only `isinfinite`/`isnan`/`isnormal`/`infinite`/`nan` exist as stubs via C registry; most libm names unsupported |

## Time, IO, control, and introspection

| Name/arity | Normative reference | Status | Tests | Caveats | Host | Evidence |
| --- | --- | --- | --- | --- | --- | --- |
| `strptime/1`, `strftime/1`, `strflocaltime/1`, `mktime/0`, `gmtime/0`, `localtime/0`, `now/0`, `fromdateiso8601/0`, `todateiso8601/0`, `fromdate/0`, `todate/0` | Manual: Builtin operators; I/O | partial | Narrow fromdate/todate/strptime/strftime paths | Date-format conversion handles only `%Y %m %d %H %M %S`; UTC/local, epochs, leap/DST open (I15) | explicit clock/timezone | Narrow cases pass |
| `input/0`, `inputs/0`, `input_filename/0`, `input_line_number/0` | Manual: I/O | unimplemented | — | Shared incremental cursor open (I17) | stdin cursor | Missing |
| `debug/0`, `debug/1`, `stderr/0` | Manual: I/O (`builtin.jq` debug) | unimplemented | — | Must go through host stderr, never ambient console | stderr descriptor | Missing |
| `error/0..1`, `halt/0..1`, `halt_error/1` | Manual: I/O; Advanced features | implemented | JqTests.Control | Explicit payloads, silent halt (exit 0), coded halt_error with raw/JSON/null stderr rendering; all propagate through handlers and loops | stderr/status | Control tests pass with upstream vectors |
| `$ENV`, `env/0`, `$ARGS`, `$__loc__`, `builtins/0`, `modulemeta/1`, `get_search_list/0`, `get_prog_origin/0`, `get_jq_origin/0` | Manual: I/O; Modules | partial | `$ARGS` populated | Only `$ARGS` (positional/named) exists; environment snapshots and introspection pending I15/I16 | explicit environment | Args tests pass |
| `tostream/0`, `fromstream/0`, `truncate_stream/1` | Manual: Streaming | unimplemented | — | Streaming helpers pending I18 | stdin/stdout bytes | Missing |
| `INDEX/2`, `JOIN/3..4`, `IN/1..2` | Manual: Builtin operators (`builtin.jq` SQL-style) | unimplemented | — | Missing; I20 closes remaining rows | none | Unsupported today |
| `path/1`, `isempty/1` (control), `getpath`/`setpath`/`delpaths` (see above) | Manual: Assignment; Advanced features | partial | JqTests.Paths | `path`/`getpath`/`setpath`/`delpaths` implemented (see rows above); `isempty/1` needs reductions (I10) | none | Path tests pass |

## Rendering, diagnostics, and exit status

| Feature | Normative reference | Status | Tests | Caveats | Host | Evidence |
| --- | --- | --- | --- | --- | --- | --- |
| Compact default, pretty indent, LF bytes | Manual: Invoking jq | partial | Compact/indent cases | Pretty uses platform newlines today; I19 must choose LF and byte-compare | stdout bytes | Needs I19 decision |
| Raw/join/NUL output, sorted keys, ASCII, color | Manual: Invoking jq | partial | Raw/join/ascii paths | NUL/sorted/color/seq pending | stdout bytes + terminal profile | Needs I19 |
| Exit categories: success, compile (3), input (4), runtime (5), system (2), halt codes, `-e` modes | Command parser exit codes | partial | Stages 2/3/4/5 covered; JqTests.Diagnostics | Unknown/wrong-arity now compile (3) via registry; host failures escape unstaged and cancellation propagates; halt/`-e` still open (I08/I19) | status/stderr | 336 tests pass |
| Diagnostics bytes and source spans | Manual + parser errors | partial | Invalid filter/option/JSON/path cases; JqTests.Diagnostics | Compile errors carry line/column (LF lines, UTF-16 columns) and program identity (`filter` vs `file "path"`); unknown names, arities, and undefined variables fail at compile; unterminated strings are explicit; runtime type errors use a structured payload type rendered as before | stderr bytes | Span/identity/variable/payload tests pass |
| Descriptor ownership, backpressure, closure, cancellation, budgets | Architecture + host contract | partial | HostBoundaryTests + memory tests + JqTests.Diagnostics host cases | Host CLR/size violations throw a distinct host failure instead of a staged `jq:` error; whole-input buffering and incremental cursor still pending I17/I21 | host descriptors | Boundary + host-failure tests pass |

## Probe and differential evidence (I01)

- Golden-case schema: `JqGoldenCase` in `tests/Lokad.Jq.Tests` carries
  case ID, argument array, UTF-8 stdin bytes, virtual files, environment,
  expected stdout/stderr bytes, and expected exit status.
- Fixed local cases always run through the public `Jq` API with an
  in-memory host. They cover one matching identity case, malformed JSON,
  Unicode scalar output, and empty output.
- Opt-in reference comparison runs only when `LOKAD_JQ_REFERENCE_JQ` is an
  absolute path to an independently installed executable. It checks
  `--version`, passes arguments via `ArgumentList` (no shell), closes stdin,
  drains stdout/stderr concurrently with bounded capture, enforces a timeout,
  and kills only its owned process tree. It never executes files from
  inspection checkouts. Randomized future cases must record seed and case ID.
- Mismatch reporting is tested with a synthetic diverging case. Timeout
  handling is tested with an OS-provided sleep/ping delay and a short timeout.
  Ordinary runs skip reference cases instead of failing when no executable
  is configured.
