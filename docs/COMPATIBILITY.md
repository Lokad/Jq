# Compatibility

Target: jq 1.8.2. Compatibility is incomplete: an implemented inventory row means
usable behavior with focused tests, not full upstream conformance. Current totals
are 95 implemented rows, eight intentionally different and one partial (regex).

The runtime uses UTF-8 I/O, Unicode-scalar string operations and `JsonNode` values
with C# null representing JSON null. Objects preserve insertion order and resolve
duplicate keys last-wins. Bindings, value parameters and pass-through operations
retain aliases, including named arguments and their `$ARGS.named` entries,
`getpath` and selected extrema. Updates rebuild
containers while preserving the tested path rules and independent source values.
All I/O, modules, environment, clock and timezone inputs come from the embedding host.
String search indices count Unicode scalars. String and array searches report
overlapping matches.
Loops preserve depth-first condition multiplicity, effects and lazy termination.
Path-mode `label`/`break`, `try` and `?` preserve earlier outputs and payload
identity. Labels retain lexical binding across shadowing and captured calls;
intervening catch/optional scopes can intercept breaks. Keyed collections require arrays after evaluating key effects; `min_by`
keeps the first minimum and `max_by` the last maximum. Slice deletion preserves
slice-relative indices and mixed path forms. Computed index/slice paths and
updates preserve the enclosing input and key-major effects/results; `?` leaves
key-expression errors visible. `join` requires array input; null
elements and empty arrays remain valid.

## Known differences and limits

- **Numbers:** double-domain semantics with integral storage. Decimal-literal
  fidelity, large-number comparisons and exponent spelling can differ from
  decimal-enabled jq builds. Special-function accuracy remains approximate. See
  [runtime number semantics](RUNTIME.md#numbers).
- **Regex:** the managed PCRE2 profile differs from jq's Oniguruma semantics;
  flags `m`/`s`/`p`, longest matching and `\C` differ. Sparse numeric captures
  have an engine limitation. See [regex behavior](REGEX.md).
- **Diagnostics and Unicode:** JSON reader wording, UTF-16 source columns and
  strict unpaired-surrogate rejection are deliberate policies. Input line numbers
  on unterminated tails can exceed jq's newline-based count by one.
- **CLI/host identity:** `-V` and `--build-configuration` identify Lokad.Jq.
  `--unbuffered`, `-M` and `-b` are inert; `-C`, internal tool switches and process
  search/origin queries are rejected. ANSI color and native OS filenames are
  outside the hosted API.
- **Modules:** hosted `import`, `include`, `-L` and `modulemeta` are supported.
  Automatic `~/.jq`, home expansion and `$ORIGIN` lookup are excluded. Main-file
  `$__loc__` reports the file path; qualified calls use hierarchical scopes.
  `modulemeta` synchronously waits for host reads during evaluation.
- **Recursion:** direct tails and proven single-valued control paths flatten.
  Recursive calls with active handlers, optional suppression or labels retain
  their scopes and use the evaluation depth safeguard. Direct filter forwarding
  stays flat; composite captured-filter chains retain the depth guard.
- **Bindings:** object-pattern key expressions can see provisional null bindings
  when the pattern shadows an outer variable used by its key expression.
- **Other edges:** NaN `range` bounds yield no values. Range validates numeric
  arguments before iteration; a nonnumeric step can error where jq yields nothing.
  Local-time conversion is constrained by .NET's date range. Partial `strptime`
  fields can retain weekday 8 / year-day 367 sentinels; C-library defaults and
  epoch directives can differ across jq builds. A whitespace boundary permits
  trailing date text, returned as the ninth element. Focused regression
  tests record accepted coercions, ordering, empty-stream behavior and error shapes.

Resource safeguards are separate from jq language errors: shared memory refusal
and structural/regex exhaustion terminate with status 5 outside `try`/`?`.
Cancellation and host failures propagate separately.
See [runtime safeguards](RUNTIME.md#safeguards-and-cancellation) for
construction limits, ownership and cleanup.

## Inventory

Tests are under `tests/Lokad.Jq.Tests`; names below identify the focused test
families. **Implemented** does not imply every edge matches upstream;
**intentionally different** follows the policies above; **partial** has remaining
semantic gaps. References are the jq 1.8.2 manual and its parser/builtin registries
(`src/main.c`, `src/builtin.c`, `src/builtin.jq`, `src/libm.h`, `src/parser.y`).

### Command options

| Feature / arity | Status | Tests |
| --- | --- | --- |
| `-n`, `--null-input` | implemented | JqTests.Inputs |
| `-R`, `--raw-input` | implemented | JqTests.Inputs |
| `-s`, `--slurp` | implemented | JqTests.Inputs |
| `-c`, `--compact-output` | implemented | JqTests |
| `-r`, `--raw-output` | implemented | JqTests |
| `-j`, `--join-output` | implemented | JqTests.Output |
| `-a`, `--ascii-output` | implemented | JqTests |
| `--tab` | implemented | JqTests |
| `--indent n` | implemented | JqTests.CommandLine |
| `--unbuffered` | intentionally different | JqTests |
| `-f`, `--from-file` | implemented | JqTests.CommandLine |
| `--arg name value` | implemented | JqTests.CommandLine |
| `--argjson name value` | implemented | JqTests.CommandLine |
| `--args`, `--jsonargs` | implemented | JqTests.CommandLine |
| `-V`, `--version` | intentionally different | JqTests |
| `--build-configuration` | intentionally different | JqTests.Output |
| `--` | implemented | JqTests.CommandLine |
| `--raw-output0` | implemented | JqTests.Output |
| `-S`, `--sort-keys` | implemented | JqTests.Output, JqTests.UnicodeOrdering |
| `-C`, `--color-output` | intentionally different | JqTests.Output |
| `-M`, `--monochrome-output` | intentionally different | JqTests.Output |
| `--seq` | implemented | JqTests.Seq |
| `--stream`, `--stream-errors` | implemented | JqTests.Streaming |
| `-L`, `--library-path dir` | implemented | JqTests.Modules |
| `--slurpfile name file` | implemented | JqTests.CommandLine |
| `--rawfile name file` | implemented | JqTests.CommandLine |
| `-e`, `--exit-status` | implemented | JqTests.Output |
| `-b`, `--binary` | intentionally different | JqTests.CommandLine |
| `-h`, `--help` | implemented | JqTests.Output |
| `--run-tests`, `--debug-dump-disasm`, `--debug-trace`, `--debug-trace=all` | intentionally different | JqTests.Output |

### Grammar and evaluation

| Feature / arity | Status | Tests |
| --- | --- | --- |
| Literals: `null`, `true`, `false`, numbers, strings | implemented | JqTests |
| Identity `.`, field `.foo`, `.["foo"]`, index `.[0]`, slice `.[1:3]`, iterator `.[]`, optional `?`, descent `..` | implemented | JqTests |
| Comma `,`, pipe `\|`, precedence, parentheses | implemented | JqTests |
| Object `{a:1}`, `{a:(1,2)}`, array `[f]` construction | implemented | JqTests |
| Arithmetic `+ - * / %`, comparisons, boolean `and`/`or`/`not`, alternative `//` | implemented | JqTests |
| String interpolation `"\(f)"`, `@format "text \(f)"` | implemented | JqTests |
| `if/then/elif/else/end` | implemented | JqTests.Conditionals |
| `as $x`, destructuring, `$ARGS`, lexical scope | implemented | JqTests.Bindings |
| `def f: ...`, `def f(a;b): ...`, closures, recursion | implemented | JqTests.Functions |
| `try/catch`, `?` suppression, `error`, `halt`, `halt_error`, `label`/`break` | implemented | JqTests.Control, JqTests.ErrorPaths |
| Path expressions, `=`, `\|=`, `//=`, arithmetic updates | implemented | JqTests.Paths, JqTests.DeletionStorage, JqTests.CompoundUpdates |
| `reduce`/`foreach`, `limit`, `first`, `last`, `nth`, `isempty`, `while`, `until`, `repeat`, `recurse`, `walk` | implemented | JqTests.Reduction, JqTests.Iteration |
| `import`/`include`, `module`, `modulemeta`, `$__loc__` | implemented | JqTests.Modules |
| Comments `#`, multiline, string escapes | implemented | JqTests |

### Values, ordering, and serialization

| Feature / arity | Status | Tests |
| --- | --- | --- |
| null vs missing vs empty stream; truthiness | implemented | JqTests |
| Equality and total ordering | implemented | JqTests |
| Number literals, precision, signed zero, non-finite | implemented | JqTests |
| Object key order, duplicate keys | implemented | JqTests |
| Unicode scalar indexing, `length`, slices | implemented | JqTests.Hardening |
| Compact vs pretty rendering, LF bytes, sorted keys, ASCII | implemented | JqTests.Output |

### Structural and collection builtins

| Feature / arity | Status | Tests |
| --- | --- | --- |
| `length/0`, `type/0` | implemented | JqTests |
| `keys/0`, `keys_unsorted/0` | implemented | JqTests.Collections |
| `has/1`, `in/1`, `inside/1`, `IN/1..2` | implemented | JqTests.Collections |
| `contains/1`, `inside/1` | implemented | JqTests.Collections |
| `map/1`, `map_values/1` | implemented | JqTests.Collections |
| `arrays/0`, `objects/0`, `iterables/0`, `booleans/0`, `numbers/0`, `strings/0`, `nulls/0`, `values/0`, `scalars/0` | implemented | JqTests.Inventory, JqTests.Collections |
| `select/1`, `empty/0`, `not/0` | implemented | JqTests.Select |
| `paths/0..1`, `path/1`, `getpath/1`, `setpath/2`, `delpaths/1`, `del/1`, `pick/1` | implemented | JqTests.Paths, JqTests.Hardening |
| `to_entries/0`, `from_entries/0`, `with_entries/1` | implemented | JqTests.Collections |
| `add/0`, `add/1`, `flatten/0..1`, `reverse/0` | implemented | JqTests.Collections |
| `sort/0`, `sort_by/1`, `group_by/1`, `unique/0`, `unique_by/1`, `min/0`, `max/0`, `min_by/1`, `max_by/1`, `bsearch/1` | implemented | JqTests.Collections, JqTests.ComparisonKeys, JqTests.ComparisonMemory |
| `range/1..3` | implemented | JqTests |
| `transpose/0`, `combinations/0..1` | implemented | JqTests.Collections, JqTests.Hardening |
| `any/0..2`, `all/0..2` | implemented | JqTests.Collections |
| `first/0..1`, `last/0..1`, `nth/1..2`, `isempty/1`, `limit/2`, `skip/2`, `while/2`, `until/2`, `repeat/1`, `recurse/0..2`, `walk/1`, `rindex/1` | implemented | JqTests.Iteration, JqTests.Collections, JqTests.Inventory, JqTests.Hardening |
| `indices/1`, `index/1`, `rindex/1` | implemented | JqTests.Collections, JqTests.StringMemory |
| `tojson/0`, `fromjson/0`, `tonumber/0`, `toboolean/0`, `tostring/0` | implemented | JqTests |
| `finites/0`, `normals/0`, `_sort_by_impl/1`, `_group_by_impl/1`, `_unique_by_impl/1`, `_min_by_impl/1`, `_max_by_impl/1`, `_flatten/1` | implemented | JqTests.Inventory |

### Strings, encodings, and formats

| Feature / arity | Status | Tests |
| --- | --- | --- |
| `utf8bytelength/0`, `explode/0`, `implode/0` | implemented | JqTests |
| `split/1` (literal) | implemented | JqTests |
| `join/1` | implemented | JqTests |
| `startswith/1`, `endswith/1`, `ltrimstr/1`, `rtrimstr/1`, `trimstr/1`, `trim/0`, `ltrim/0`, `rtrim/0` | implemented | JqTests |
| `ascii_downcase/0`, `ascii_upcase/0` | implemented | JqTests |
| `@text`, `@json`, `@html`, `@uri`, `@urid`, `@csv`, `@tsv`, `@sh`, `@base64`, `@base64d` | implemented | JqTests.Formatting |
| Formatted interpolation `@fmt "\(f)"` | implemented | JqTests |
| `format/1`, `_strindices/1`, `_negate/0` | implemented | JqTests.Inventory |

### Regex

| Feature / arity | Status | Tests |
| --- | --- | --- |
| `test/1..2` | implemented | JqTests.Test |
| `gsub/2..3` | implemented | JqTests.Gsub |
| `match/1..2`, `capture/1..2`, `scan/1..2`, `splits/1..2`, `split/2`, `sub/2..3` | implemented | JqTests.Match |
| `_match_impl/3` | implemented | JqTests.Inventory |
| Flags, captures, scalar offsets, advanced syntax | partial | JqTests |

### Math

| Feature / arity | Status | Tests |
| --- | --- | --- |
| `abs/0`, `floor/0`, `ceil/0`, `round/0`, `trunc/0`, `nearbyint/0`, `rint/0`, `fabs/0`, `sqrt/0`, `cbrt/0` | implemented | JqTests.Math |
| `exp/0`, `exp2/0`, `exp10/0`, `log/0`, `log10/0`, `log2/0`, `pow/2`, `atan2/2`, `hypot/2`, `sin/0`, `cos/0`, `tan/0`, `asin/0`, `acos/0`, `atan/0`, `sinh/0`, `cosh/0`, `tanh/0`, `asinh/0`, `acosh/0`, `atanh/0` | implemented | JqTests.Math |
| Classification (`isinfinite/0`, `isnan/0`, `isnormal/0`, `isfinite/0`, `infinite/0`, `nan/0`) and `have_decnum/0`, `have_literal_numbers/0` | implemented | JqTests.Math |
| `remainder/2`, `drem/2`, `fmod/2`, `fmax/2`, `fmin/2`, `fdim/2`, `copysign/2`, `nextafter/2`, `nexttoward/2`, `scalb/2`, `scalbln/2`, `ldexp/2`, `fma/3` | implemented | JqTests.Math |
| `modf/0`, `frexp/0`, `lgamma_r/0` | implemented | JqTests.Math |
| `logb/0`, `log1p/0`, `expm1/0`, `significand/0` | implemented | JqTests.Math |
| `tgamma/0`, `gamma/0`, `lgamma/0`, `erf/0`, `erfc/0` | implemented | JqTests.Math |
| `j0/0`, `j1/0`, `y0/0`, `y1/0`, `jn/2`, `yn/2` | implemented | JqTests.Math |

### Time, IO, control, and introspection

| Feature / arity | Status | Tests |
| --- | --- | --- |
| `strptime/1`, `strftime/1`, `strflocaltime/1`, `mktime/0`, `gmtime/0`, `localtime/0`, `now/0`, `fromdateiso8601/0`, `todateiso8601/0`, `fromdate/0`, `todate/0` | implemented | JqTests.Date |
| `input/0`, `inputs/0`, `input_filename/0`, `input_line_number/0` | implemented | JqTests.Inputs |
| `debug/0`, `debug/1`, `stderr/0` | implemented | JqTests.Time |
| `error/0..1`, `halt/0`, `halt_error/0..1` | implemented | JqTests.Control, JqTests.ErrorPaths |
| `$ENV`, `env/0`, `$ARGS`, `$__loc__`, `builtins/0`, `modulemeta/0` | implemented | JqTests.Time, JqTests.Modules, JqTests.Inventory |
| `get_search_list/0`, `get_prog_origin/0`, `get_jq_origin/0` | intentionally different | JqTests.Inventory |
| `tostream/0`, `fromstream/1`, `truncate_stream/1` | implemented | JqTests.Streaming |
| `INDEX/1..2`, `JOIN/2..4` | implemented | JqTests.Inventory |
| `_assign/2`, `_modify/2`, `_repeat/0`, `_until/0`, `_while/0` | implemented | JqTests.Inventory |
| `_plus/2`, `_minus/2`, `_multiply/2`, `_divide/2`, `_mod/2`, `_equal/2`, `_notequal/2`, `_less/2`, `_lesseq/2`, `_greater/2`, `_greatereq/2` | implemented | JqTests.Inventory |

### Rendering, diagnostics, and exit status

| Feature / arity | Status | Tests |
| --- | --- | --- |
| Pretty default, indentation, LF bytes | implemented | JqTests.Output |
| Raw/join/NUL output, sorted keys, ASCII | implemented | JqTests.Output |
| Exit categories: success, compile (3), input parse and quota failures (5), missing files and system errors (2), halt codes, `-e` modes | implemented | JqTests.Diagnostics, JqTests.Output |
| Diagnostics bytes and source spans | implemented | JqTests.Diagnostics |
| Descriptor ownership, backpressure, closure, cancellation, budgets | implemented | HostBoundaryTests, JqTests.Diagnostics, JqTests.Hardening, JqTests.Execution, JqTests.Governed |

## Evidence

The recorded packaged value campaign matches 703/734 vectors, with all 31 misses
classified; regex and encoding sweeps match 66/66 and 32/32. These are bounded
historical campaigns, not a full conformance suite.

Deterministic public-API tests run without jq. Opt-in differential tests require
`LOKAD_JQ_REFERENCE_JQ` to name an independently installed executable outside
`external/`; they compare outputs/status with bounded capture and timeouts.
Seeded fuzz tests retain reproducible seeds and case IDs. New compatibility fixes
need focused upstream evidence and exact stream/status or byte regressions.
