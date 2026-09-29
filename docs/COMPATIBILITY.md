# Compatibility baseline

The target for further work is upstream jq 1.8.2. This extraction is a partial
implementation and has not passed an upstream conformance suite. Its command
tests document the initial implementation behavior, including historical
limits and divergences that future increments must resolve explicitly.

Present building blocks include field/index/iteration access, arrays/objects,
comma/pipe streams, arithmetic/comparison/boolean expressions, alternatives,
conditionals, variables supplied as arguments, interpolation, basic functions,
formatters, `test`/`gsub`, raw/slurped/null input, filter files, and output options.
Their full upstream semantics and overloads are not implied by their presence.

Known gaps to investigate and close:

- User-defined functions, lexical variable binding/destructuring, reductions,
  recursion/control flow, assignment/update paths, modules, and many builtins.
- Generic function arguments currently collect streams and often select the
  first value. jq generator semantics require a broader evaluator contract.
- Default output is compact; upstream-compatible formatting needs separate work.
  Pretty JSON currently uses platform newlines inside formatted values.
- Duplicate object keys resolve last-wins. Numbers use doubles with integral storage for integers;
  literal precision, ordering, and non-finite rendering follow docs/NUMERIC_PROFILE.md, with deliberate decimal-build divergences recorded there.
- `ascii_upcase`/`ascii_downcase` currently call Unicode case conversion.
- Regex support uses PCRE.NET. jq's Oniguruma syntax, flags, captures, offsets,
  substitutions, and edge cases need explicit compatibility evidence.
- Several CLI options, input streaming/sequence modes, exit-status behavior,
  module search paths, environment/time capabilities, and diagnostics are missing.
- Canonical virtual paths reject controls and traversal above root. Hosts own
  file access policy; native OS filename support is not yet a compatibility claim.
- Runtime input is buffered per source, not parsed incrementally.

## Current resource policy

Budgets are cumulative per execution, not a measurement of total managed memory.
Current constants include 16 MiB input, 32 MiB output, 8 Mi UTF-16 code units per
string, 256 MiB cumulative allocation allowance, 262,144 value nodes, JSON/parser
depth 64, filter length 1 Mi characters, and 4,096 tokens/command arguments.
Regex patterns and native work have additional limits in `JqRegexCache`.

These limits remain useful for embedding. A complete implementation should expose
an explicit execution policy and distinguish exhaustion/cancellation from
catchable jq errors; finite policy limits must not excuse missing language features.

## Conformance work

Track each language feature, builtin name/arity, CLI option, manual section,
and upstream test family in a compatibility matrix. Do not count a skipped test
as supported behavior. Compare exact stream ordering and status, and use byte
comparisons where formatting is the feature under test. Record the reference
version and environment for every differential result.
